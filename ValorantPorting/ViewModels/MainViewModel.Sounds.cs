using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ValorantPorting.AppUtils;
using ValorantPorting.Export;
using ValorantPorting.Services;
using ValorantPorting.Services.Endpoints;
using ValorantPorting.Views.Controls;

namespace ValorantPorting.ViewModels;

// The Sounds tab: every sound effect and voice line of the game, searchable by plain names; listen, save as .wav, or
// put it in Blender's timeline. Only names are loaded for the list; a sound's audio is read when it's picked.
public partial class MainViewModel
{
    [ObservableProperty] private ObservableCollection<SoundItem> sounds = new();
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SelectedSoundVisibility), nameof(NoSoundSelectedVisibility))]
    private SoundItem? selectedSound;
    [ObservableProperty] private ObservableCollection<SoundVariantRow> soundVariants = new();
    [ObservableProperty] private SoundVariantRow? selectedSoundVariant;
    [ObservableProperty] private List<string> soundLanguages = [];
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SoundLanguageVisibility))] private string? soundLanguage;
    [ObservableProperty] private string soundStatus = "";

    public string[] SoundCategories { get; } = ["All sounds", "Weapons", "Agents", "Voice lines", "Maps", "Interface", "Game modes", "Music", "Other"];
    [ObservableProperty] private string soundCategory = "All sounds";
    public event Action? SoundsChanged;
    partial void OnSoundCategoryChanged(string value) => SoundsChanged?.Invoke();

    public Visibility SoundPanelVisibility => ActiveTab == EAssetType.Sound ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SelectedSoundVisibility => SelectedSound is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility NoSoundSelectedVisibility => SelectedSound is null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SoundLanguageVisibility => SoundLanguages.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

    private bool soundsLoaded;
    private List<GameSounds.Variant> allVariants = [];
    private readonly MediaPlayer player = new();
    private int soundSelection; // picks made while an earlier one still loads are what count

    public bool MatchesSoundCategory(SoundItem item) => SoundCategory == "All sounds" || item.Category == SoundCategory ||
        SoundCategory == "Other" && !SoundCategories.Contains(item.Category);

    public void LoadSounds()
    {
        if (soundsLoaded || AppVM.CUE4ParseVM is null) return;
        soundsLoaded = true;
        var provider = AppVM.CUE4ParseVM.Provider;
        Task.Run(() =>
        {
            ValorantNames.WaitUntilLoaded(TimeSpan.FromSeconds(10));
            SoundAbilities? abilities = null;
            try
            {
                abilities = new SoundAbilities(provider, ValorantNames.Agents);
            }
            catch (Exception ex)
            {
                AppLog.Warning($"Sounds: ability names unavailable ({ex.Message}).");
            }

            var maps = Export.SoundNamer.MapCodenames(ValorantNames.Maps.Select(m => (m.Name, m.MapUrl)));
            var items = GameSounds.List(provider).Select(e => new SoundItem(e, abilities, maps))
                .OrderBy(i => i.Category == "Other").ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase).ToList();
            Application.Current.Dispatcher.Invoke(() =>
            {
                Sounds = new ObservableCollection<SoundItem>(items);
                SoundsChanged?.Invoke();
                AppLog.Information($"Sound list loaded: {items.Count} sounds.");
            });
        });
    }

    partial void OnSelectedSoundChanged(SoundItem? value)
    {
        player.Stop();
        SoundVariants.Clear();
        SoundLanguages = [];
        SoundLanguage = null;
        allVariants = [];
        SoundStatus = "";
        if (value is null) return;

        var selection = ++soundSelection;
        SoundStatus = "Loading...";
        var provider = AppVM.CUE4ParseVM.Provider;
        Task.Run(() =>
        {
            List<GameSounds.Variant> variants;
            try
            {
                variants = GameSounds.Variants(provider, value.EventPath);
            }
            catch (Exception ex)
            {
                variants = [];
                AppLog.Warning($"Could not read the sound {value.EventName}: {ex.Message}");
            }

            Application.Current.Dispatcher.Invoke(() =>
            {
                if (selection != soundSelection) return;
                allVariants = variants;
                var languages = variants.Select(v => v.Language).OfType<string>().Distinct().OrderBy(l => l).ToList();
                SoundLanguages = languages;
                SoundLanguage = languages.Count == 0 ? null : languages.FirstOrDefault(l => l.Equals("en-US", StringComparison.OrdinalIgnoreCase)) ?? languages[0];
                ShowVariants();
                SoundStatus = variants.Count == 0
                    ? "This one has no audio of its own (it only starts or changes other sounds)."
                    : "";
                if (SoundVariants.Count > 0) SelectedSoundVariant = SoundVariants[0];
            });
        });
    }

    partial void OnSoundLanguageChanged(string? value) => ShowVariants();

    private void ShowVariants()
    {
        var shown = allVariants.Where(v => SoundLanguage is null || v.Language is null || v.Language == SoundLanguage).ToList();
        SoundVariants = new ObservableCollection<SoundVariantRow>(shown.Select((v, i) => new SoundVariantRow(v, i + 1)));
        SelectedSoundVariant = SoundVariants.FirstOrDefault();
        OnPropertyChanged(nameof(SoundLanguageVisibility));
    }

    // the selected version as a .wav (converted the first time)
    private async Task<string?> SelectedWav()
    {
        if (SelectedSound is not { } sound || SelectedSoundVariant is not { } row) return null;
        var wav = await Task.Run(() => GameSounds.Wav(sound.EventPath, row.Variant));
        if (wav is null) SoundStatus = "This sound couldn't be converted (see the log).";
        return wav;
    }

    [RelayCommand]
    public async Task PlaySound()
    {
        if (await SelectedWav() is not { } wav) return;
        player.Open(new Uri(wav));
        player.Play();
        UserLibrary.AddRecent(SelectedSound!.LibraryId);
    }

    [RelayCommand]
    public void StopSound() => player.Stop();

    [RelayCommand]
    public async Task SaveSound()
    {
        if (SelectedSound is not { } sound || await SelectedWav() is not { } wav) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save sound", Filter = "WAV audio|*.wav", FileName = SafeFileName(sound.Title) + ".wav"
        };
        if (dialog.ShowDialog() != true) return;
        File.Copy(wav, dialog.FileName, overwrite: true);
        AppLog.Information($"Saved {sound.Title} to {dialog.FileName}");
    }

    [RelayCommand]
    public async Task SaveAllSoundVersions()
    {
        if (SelectedSound is not { } sound || SoundVariants.Count == 0) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Save every version of the sound into a folder" };
        if (dialog.ShowDialog() != true) return;
        var saved = 0;
        foreach (var row in SoundVariants.ToList())
        {
            if (await Task.Run(() => GameSounds.Wav(sound.EventPath, row.Variant)) is not { } wav) continue;
            var name = SoundVariants.Count > 1 ? $"{SafeFileName(sound.Title)} ({row.Label.ToLowerInvariant()}).wav" : $"{SafeFileName(sound.Title)}.wav";
            File.Copy(wav, Path.Combine(dialog.FolderName, name), overwrite: true);
            saved++;
        }

        AppLog.Information($"Saved {saved} version(s) of {sound.Title} to {dialog.FolderName}");
    }

    [RelayCommand]
    public async Task SendSoundToBlender()
    {
        if (SelectedSound is not { } sound || await SelectedWav() is not { } wav) return;
        BlenderService.SendSound(sound.Title, [wav]);
        UserLibrary.AddRecent(sound.LibraryId);
        AppLog.Information($"Sent {sound.Title} to BLENDER (Video Sequencer, at the current frame).");
    }

    private static string SafeFileName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c)).Replace('·', '-').Trim();
}
