using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WE_Tool.Models;

namespace WE_Tool.ViewModels.Controls.Papers
{
    public partial class RatingViewModel : ObservableObject
    {
        private bool _isBatchUpdating = false;

        [ObservableProperty]
        public partial bool RatingExpander { get; set; }

        [ObservableProperty] public partial bool G { get; set; }

        [ObservableProperty] public partial bool Pg { get; set; }

        [ObservableProperty] public partial bool R { get; set; }

        public void LoadFromSettings(PapersConfig.Expander expander)
        {
            _isBatchUpdating = true;

            RatingExpander = expander.RatingExpander;

            G = expander.G;
            Pg = expander.Pg;
            R = expander.R;

            _isBatchUpdating = false;
        }
        public async Task ResetFiltersAsync(bool selectmode)
        {
            _isBatchUpdating = true;

            G = selectmode;
            Pg = selectmode;
            R = selectmode;

            _isBatchUpdating = false;
            RaisePropertiesChanged();
        }
        public void RaisePropertiesChanged()
        {
            OnPropertyChanged(string.Empty);
        }
    }
}
