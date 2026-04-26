using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WE_Tool.Helper;
using WE_Tool.Models;
using Windows.Services.Maps;

namespace WE_Tool.ViewModels
{
    public partial class PapersViewModel : ObservableObject
    {
        public Controls.Papers.PapersControlViewModel PapersControl { get; private set; } = null!;
        public IRelayCommand<string> ChangeSortCommand { get; }
        public PapersViewModel()
        {
            ChangeSortCommand = new RelayCommand<string>(PapersControl.ExecuteChangeSort);
        }
    }
}
