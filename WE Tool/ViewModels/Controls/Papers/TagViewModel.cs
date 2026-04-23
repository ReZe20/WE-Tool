using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WE_Tool.Models;

namespace WE_Tool.ViewModels.Controls.Papers
{
    public partial class TagViewModel : ObservableObject
    {
        private bool _isBatchUpdating = false;

        [ObservableProperty]
        public partial bool TagsExpander { get; set; }

        [ObservableProperty] public partial bool Abstract { get; set; }
        [ObservableProperty] public partial bool Animal { get; set; }
        [ObservableProperty] public partial bool Anime { get; set; }
        [ObservableProperty] public partial bool Cartoon { get; set; }
        [ObservableProperty] public partial bool Cgi { get; set; }
        [ObservableProperty] public partial bool Cyberpunk { get; set; }
        [ObservableProperty] public partial bool Fantasy { get; set; }
        [ObservableProperty] public partial bool Game { get; set; }
        [ObservableProperty] public partial bool Girls { get; set; }
        [ObservableProperty] public partial bool Guys { get; set; }
        [ObservableProperty] public partial bool Landscape { get; set; }
        [ObservableProperty] public partial bool Medieval { get; set; }
        [ObservableProperty] public partial bool Memes { get; set; }
        [ObservableProperty] public partial bool Mmd { get; set; }
        [ObservableProperty] public partial bool Music { get; set; }
        [ObservableProperty] public partial bool Nature { get; set; }
        [ObservableProperty] public partial bool Pixelart { get; set; }
        [ObservableProperty] public partial bool Relaxing { get; set; }
        [ObservableProperty] public partial bool Retro { get; set; }
        [ObservableProperty] public partial bool SciFi { get; set; }
        [ObservableProperty] public partial bool Sports { get; set; }
        [ObservableProperty] public partial bool Technology { get; set; }
        [ObservableProperty] public partial bool Television { get; set; }
        [ObservableProperty] public partial bool Vehicle { get; set; }
        [ObservableProperty] public partial bool Unspecified { get; set; }

        public void LoadFromSettings(PapersConfig.Expander expander)
        {
            _isBatchUpdating = true;

            TagsExpander = expander.TagsExpander;
            
            Abstract = expander.Abstract;
            Animal = expander.Animal;
            Anime = expander.Anime;
            Cartoon = expander.Cartoon;
            Cgi = expander.Cgi;
            Cyberpunk = expander.Cyberpunk;
            Fantasy = expander.Fantasy;
            Game = expander.Game;
            Girls = expander.Girls;
            Guys = expander.Guys;
            Landscape = expander.Landscape;
            Medieval = expander.Medieval;
            Memes = expander.Memes;
            Mmd = expander.Mmd;
            Music = expander.Music;
            Nature = expander.Nature;
            Pixelart = expander.Pixelart;
            Relaxing = expander.Relaxing;
            Retro = expander.Retro;
            SciFi = expander.SciFi;
            Sports = expander.Sports;
            Technology = expander.Technology;
            Television = expander.Television;
            Vehicle = expander.Vehicle;
            Unspecified = expander.Unspecified;

            _isBatchUpdating = false;
        }
        public async Task ResetFiltersAsync(bool selectmode)
        {
            _isBatchUpdating = true;

            Abstract = selectmode;
            Animal = selectmode;
            Anime = selectmode;
            Cartoon = selectmode;
            Cgi = selectmode;
            Cyberpunk = selectmode;
            Fantasy = selectmode;
            Game = selectmode;
            Girls = selectmode;
            Guys = selectmode;
            Landscape = selectmode;
            Medieval = selectmode;
            Memes = selectmode;
            Mmd = selectmode;
            Music = selectmode;
            Nature = selectmode;
            Pixelart = selectmode;
            Relaxing = selectmode;
            Retro = selectmode;
            SciFi = selectmode;
            Sports = selectmode;
            Technology = selectmode;
            Television = selectmode;
            Vehicle = selectmode;
            Unspecified = selectmode;

            _isBatchUpdating = false;
            RaisePropertiesChanged();
        }
        public void RaisePropertiesChanged()
        {
            OnPropertyChanged(string.Empty);
        }
    }
}
