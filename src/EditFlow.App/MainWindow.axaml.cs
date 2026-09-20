using Avalonia.Controls;
#if DEBUG
using Avalonia;
#endif

namespace EditFlow.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
#if DEBUG
        this.AttachDevTools();
#endif
    }
}
