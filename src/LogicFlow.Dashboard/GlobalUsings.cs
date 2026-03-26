// Global using directives to resolve WPF/WinForms namespace ambiguity
// Since we use WinForms only for NotifyIcon (system tray), default to WPF types

global using Application = System.Windows.Application;
global using Brush = System.Windows.Media.Brush;
global using Brushes = System.Windows.Media.Brushes;
global using Color = System.Windows.Media.Color;
global using Point = System.Windows.Point;
global using Size = System.Windows.Size;
global using FontFamily = System.Windows.Media.FontFamily;
global using MessageBox = System.Windows.MessageBox;
