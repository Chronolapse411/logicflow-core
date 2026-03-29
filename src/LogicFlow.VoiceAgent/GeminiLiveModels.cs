// ─────────────────────────────────────────────────────────────────────────────
// LogicFlow.VoiceAgent — Real-Time Voice AI Engine
// Powered by Gemini 3.1 Flash Live API (Live API / WebSocket streaming)
// 
// What this does:
//   1. Opens a WebSocket connection to the Gemini Live API
//   2. Streams microphone audio to the model in real-time
//   3. Receives voice responses back (audio + text transcripts)
//   4. Handles function calls — the AI can invoke any LogicFlow module
//      by voice (e.g., user says "clean my junk files" → JunkCleanerEngine runs)
//   5. Speaks responses back to the user via Windows TTS as fallback
//
// Architecture: Gemini 3.1 Flash Live → WebSocket → VoiceSessionEngine
//               → FunctionDispatcher → LogicFlow Modules
//
// Gemini Live API docs: https://ai.google.dev/api/live
// ─────────────────────────────────────────────────────────────────────────────

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace LogicFlow.VoiceAgent;

// ────────────────────────────────────────────────────────────────────────────
// Gemini Live API Wire Protocol Models
// Ref: https://ai.google.dev/api/live#request-body
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Setup message sent at the start of every Live API WebSocket session.
/// Configures the model, system instruction, function declarations, and audio format.
/// </summary>
public sealed class LiveSetup
{
    [JsonPropertyName("setup")]
    public SetupBody Setup { get; init; } = new();

    public sealed class SetupBody
    {
        [JsonPropertyName("model")]
        public string Model { get; init; } = "models/gemini-3.1-flash-live";

        [JsonPropertyName("generation_config")]
        public GenerationConfig GenerationConfig { get; init; } = new();

        [JsonPropertyName("system_instruction")]
        public SystemInstruction SystemInstruction { get; init; } = new();

        [JsonPropertyName("tools")]
        public List<ToolDeclaration> Tools { get; init; } = new();
    }
}

public sealed class GenerationConfig
{
    [JsonPropertyName("response_modalities")]
    public List<string> ResponseModalities { get; init; } = ["AUDIO"];

    [JsonPropertyName("speech_config")]
    public SpeechConfig SpeechConfig { get; init; } = new();

    /// <summary>
    /// Thinking budget: "none" | "low" | "medium" | "high"
    /// Use "low" for fast conversational responses in interactive mode.
    /// Use "medium" for repair tasks that need reasoning.
    /// </summary>
    [JsonPropertyName("thinking_config")]
    public ThinkingConfig ThinkingConfig { get; init; } = new();
}

public sealed class SpeechConfig
{
    [JsonPropertyName("voice_config")]
    public VoiceConfig VoiceConfig { get; init; } = new();
}

public sealed class VoiceConfig
{
    [JsonPropertyName("prebuilt_voice_config")]
    public PrebuiltVoiceConfig PrebuiltVoiceConfig { get; init; } = new() { VoiceName = "Charon" };
}

public sealed class PrebuiltVoiceConfig
{
    [JsonPropertyName("voice_name")]
    public string VoiceName { get; init; } = "Charon";
}

public sealed class ThinkingConfig
{
    [JsonPropertyName("thinking_budget")]
    public string ThinkingBudget { get; init; } = "low"; // "none"|"low"|"medium"|"high"
}

public sealed class SystemInstruction
{
    [JsonPropertyName("parts")]
    public List<Part> Parts { get; init; } = new()
    {
        new Part
        {
            Text = """
                   You are LogicFlow — a sovereign Windows AI maintenance agent built by DelgadoLogic.
                   You have access to 12 system tools. Your job is to diagnose and fix Windows problems
                   in real-time, conversationally.

                   Guidelines:
                   - Be direct and concise. This is a voice interface — speak like a calm expert.
                   - Always confirm before running any destructive action (clean, delete, repair).
                   - After running a tool, summarize what was found or done in 1-2 sentences.
                   - Never mention Gemini or Google. You are LogicFlow.
                   - If the user seems upset (slow PC, crashes), acknowledge briefly then fix.
                   - Use metric units: GB not GiB, seconds not milliseconds in speech.

                   Example:
                   User: "My PC is really slow lately"
                   You: "Let me check. I'll scan junk files and startup items now."
                   [call: quick_junk_scan, analyze_startup]
                   You: "Found 3.2 GB of junk files and 5 high-impact startup programs.
                         Want me to clean everything?"
                   """
        }
    };
}

public sealed class Part
{
    [JsonPropertyName("text")]
    public string Text { get; init; } = "";
}

public sealed class ToolDeclaration
{
    [JsonPropertyName("function_declarations")]
    public List<FunctionDeclaration> FunctionDeclarations { get; init; } = new();
}

public sealed class FunctionDeclaration
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    [JsonPropertyName("parameters")]
    public FunctionParameters? Parameters { get; init; }
}

public sealed class FunctionParameters
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "object";

    [JsonPropertyName("properties")]
    public Dictionary<string, ParameterProperty> Properties { get; init; } = new();

    [JsonPropertyName("required")]
    public List<string> Required { get; init; } = new();
}

public sealed class ParameterProperty
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "string";

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    [JsonPropertyName("enum")]
    public List<string>? Enum { get; init; }
}

// ── Realtime Input (audio chunks sent to the model) ────────────────────────

public sealed class RealtimeInput
{
    [JsonPropertyName("realtimeInput")]
    public RealtimeInputBody Body { get; init; } = new();

    public sealed class RealtimeInputBody
    {
        [JsonPropertyName("mediaChunks")]
        public List<MediaChunk> MediaChunks { get; init; } = new();
    }

    public sealed class MediaChunk
    {
        /// <summary>audio/pcm;rate=16000 — 16kHz mono PCM16</summary>
        [JsonPropertyName("mimeType")]
        public string MimeType { get; init; } = "audio/pcm;rate=16000";

        /// <summary>Base64-encoded PCM16 audio bytes</summary>
        [JsonPropertyName("data")]
        public string Data { get; init; } = "";
    }
}

// ── Server Response Models ──────────────────────────────────────────────────

public sealed class LiveResponse
{
    [JsonPropertyName("serverContent")]
    public ServerContent? ServerContent { get; set; }

    [JsonPropertyName("toolCall")]
    public ToolCall? ToolCall { get; set; }

    [JsonPropertyName("setupComplete")]
    public JsonElement? SetupComplete { get; set; }
}

public sealed class ServerContent
{
    [JsonPropertyName("modelTurn")]
    public ModelTurn? ModelTurn { get; set; }

    [JsonPropertyName("turnComplete")]
    public bool TurnComplete { get; set; }
}

public sealed class ModelTurn
{
    [JsonPropertyName("parts")]
    public List<ResponsePart> Parts { get; set; } = new();
}

public sealed class ResponsePart
{
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("inlineData")]
    public InlineData? InlineData { get; set; }
}

public sealed class InlineData
{
    [JsonPropertyName("mimeType")]
    public string MimeType { get; set; } = "";

    [JsonPropertyName("data")]
    public string Data { get; set; } = ""; // base64 audio
}

public sealed class ToolCall
{
    [JsonPropertyName("functionCalls")]
    public List<FunctionCall> FunctionCalls { get; set; } = new();
}

public sealed class FunctionCall
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("args")]
    public JsonElement Args { get; set; }
}

// ── Tool Result (sent back to model after executing a function call) ────────

public sealed class ToolResponse
{
    [JsonPropertyName("toolResponse")]
    public ToolResponseBody Body { get; init; } = new();

    public sealed class ToolResponseBody
    {
        [JsonPropertyName("functionResponses")]
        public List<FunctionResponse> FunctionResponses { get; init; } = new();
    }

    public sealed class FunctionResponse
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = "";

        [JsonPropertyName("name")]
        public string Name { get; init; } = "";

        [JsonPropertyName("response")]
        public object Response { get; init; } = new { output = "" };
    }
}
