using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using WE_Tool.Models;
using static System.Formats.Asn1.AsnWriter;

namespace WE_Tool.ViewModels.Controls.Papers
{
    public partial class SourceViewModel : ObservableObject
    {
        private bool _isBatchUpdating = false;

        [ObservableProperty]
        public partial bool SourceExpander { get; set; }

        [ObservableProperty] public partial bool Official { get; set; }
        [ObservableProperty] public partial bool Workshop { get; set; }
        [ObservableProperty] public partial bool Mine { get; set; }
        public void LoadFromSettings(PapersConfig.Expander expander)
        {
            _isBatchUpdating = true;

            SourceExpander = expander.TypeExpander;

            Official = expander.Scene;
            Workshop = expander.Video;
            Mine = expander.Web;

            _isBatchUpdating = false;
        }
        public async Task ResetFiltersAsync(bool selectmode)
        {
            _isBatchUpdating = true;

            Official = selectmode;
            Workshop = selectmode;
            Mine = selectmode;

            _isBatchUpdating = false;
            RaisePropertiesChanged();
        }
        public void RaisePropertiesChanged()
        {
            OnPropertyChanged(string.Empty);
        }
    }
}
