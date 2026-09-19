// Resolve ambiguities between System.Drawing and System.Windows namespaces
// when both UseWPF and UseWindowsForms are enabled.
global using Application = System.Windows.Application;
global using Brush = System.Windows.Media.Brush;
global using Brushes = System.Windows.Media.Brushes;
global using Pen = System.Windows.Media.Pen;
global using Point = System.Windows.Point;
