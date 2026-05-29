// Global type aliases that resolve the WPF vs WinForms collision.
//
// We use WPF for UI and System.Windows.Forms.NotifyIcon for the tray icon,
// so both namespaces are implicitly imported. Several type names exist in
// both namespaces — bare references would be ambiguous. These aliases pin
// each ambiguous name to the WPF variant we want.
//
// Anywhere the WinForms variant is needed (only TrayIconManager.cs), it's
// referenced via the local `using WinForms = System.Windows.Forms;` alias.

global using Application = System.Windows.Application;
global using MessageBox  = System.Windows.MessageBox;
global using UserControl = System.Windows.Controls.UserControl;
global using Button      = System.Windows.Controls.Button;
global using TextBox     = System.Windows.Controls.TextBox;
global using CheckBox    = System.Windows.Controls.CheckBox;
global using Control     = System.Windows.Controls.Control;

// Graphics-side aliases — used by TrayIconManager to render the icon bitmap.
// These pin to System.Drawing rather than System.Windows's typography variants.
global using FontStyle   = System.Drawing.FontStyle;
global using FontFamily  = System.Drawing.FontFamily;
