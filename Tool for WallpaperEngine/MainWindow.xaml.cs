using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.Foundation;
using Windows.Foundation.Collections;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Tool_for_WallpaperEngine
{
    /// <summary>
    /// An empty window that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            ExtendsContentIntoTitleBar = true;
        }

        private void nvSample_ItemInvoked(NavigationView sneder, NavigationViewItemInvokedEventArgs args)
        {
            string tag = args.InvokedItemContainer.Tag.ToString();

            switch (tag)
            {
                case "Papers":
                    contentFrame.Navigate(typeof(Papers), null);
                    break;
                case "LoadPapers":
                    contentFrame.Navigate(typeof(LoadPapers), null);
                    break;
                case "Info":
                    contentFrame.Navigate(typeof(Info), null);
                    break;
                case "Settings":
                    contentFrame.Navigate(typeof(Settings), null);
                    break;
            }
        }
        private void nvSample_Loaded(object sender, RoutedEventArgs e)
        {
            contentFrame.Navigate(typeof(Papers), null);
        }
        private void NavigateTo(Type pageType, object? parameter)
        {
            if (contentFrame.Content != null && contentFrame.Content.GetType() == pageType)
            {
                return;
            }
            contentFrame.Navigate(pageType, parameter);
        }
    }
}
 