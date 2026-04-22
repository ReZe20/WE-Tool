using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WE_Tool.Models;

namespace WE_Tool.ViewModels.Controls
{
    public partial class TypesViewModel : ObservableObject
    {
        private bool _isBatchUpdating = false;

        [ObservableProperty]
        public partial bool TypeExpander { get; set; }

        [ObservableProperty] public partial bool Scene { get; set; }

        [ObservableProperty] public partial bool Video { get; set; }

        [ObservableProperty] public partial bool Web { get; set; }

        [ObservableProperty] public partial bool Application { get; set; }

        [ObservableProperty] public partial bool Preset { get; set; }

        [ObservableProperty] public partial bool Unknown { get; set; }

        public void LoadFromSettings(PapersConfig.Expander expander)
        {
            _isBatchUpdating = true;

            TypeExpander = expander.TypeExpander;

            Scene = expander.Scene;
            Video = expander.Video;
            Web = expander.Web;
            Application = expander.Application;
            Preset = expander.Preset;
            Unknown = expander.Unknown;

            _isBatchUpdating = false;
        }
        public async Task ResetFiltersAsync(bool selectmode)
        {
            _isBatchUpdating = true;

            Scene = selectmode;
            Video = selectmode;
            Web = selectmode;
            Application = selectmode;
            Preset = selectmode;
            Unknown = selectmode;

            _isBatchUpdating = false;
            RaisePropertiesChanged();
        }
        public void RaisePropertiesChanged()
        {
            OnPropertyChanged(string.Empty);
        }
    }
}
