using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sightline.Core.Camera;
using Sightline.Protocol.GpSock;

namespace Sightline.App.ViewModels;

/// <summary>One file on the card, as the Library page shows it.</summary>
public sealed partial class CardItem : ObservableObject, IDisposable
{
    private byte[]? thumbnailFrom;

    internal CardItem(CameraFile file, string day, bool firstOfDay)
    {
        File = file;
        Day = day;
        FirstOfDay = firstOfDay;
    }

    /// <summary>The file.</summary>
    public CameraFile File { get; }

    /// <summary>Its name, in the camera's style.</summary>
    public string Name => File.DisplayName;

    /// <summary>Its kind, in a word.</summary>
    public string Kind => CardWords.Kind(File.Kind);

    /// <summary>Its size, as people say it.</summary>
    public string Size => CardWords.Size(File.ApproximateBytes);

    /// <summary>It, as a screen reader says it.</summary>
    public string Description => CardWords.Describe(File);

    /// <summary>The day it was taken.</summary>
    public string Day { get; }

    /// <summary>Whether it is the first of its day, where the day's heading goes.</summary>
    public bool FirstOfDay { get; }

    /// <summary>Its thumbnail, once the camera has sent one.</summary>
    [ObservableProperty]
    private Bitmap? thumbnail;

    /// <summary>Where a copy of it is, or null when none was asked for.</summary>
    [ObservableProperty]
    private string? transfer;

    /// <summary>Whether it is picked for copying or deleting.</summary>
    [ObservableProperty]
    private bool selected;

    /// <inheritdoc />
    public void Dispose() => Thumbnail?.Dispose();

    /// <summary>Shows <paramref name="jpeg"/> as the thumbnail, decoding it only when it is new.</summary>
    internal void ShowThumbnail(byte[] jpeg, Func<byte[], Bitmap?> decode)
    {
        if (ReferenceEquals(jpeg, thumbnailFrom))
        {
            return;
        }

        thumbnailFrom = jpeg;
        var previous = Thumbnail;
        Thumbnail = decode(jpeg);
        previous?.Dispose();
    }
}

/// <summary>
/// The camera's card: its files by day with their thumbnails, playing its videos, copying them to this PC, and
/// deleting them from the card after a confirmation.
/// </summary>
public sealed partial class LibraryViewModel : ObservableObject, IDisposable
{
    private readonly AppParts parts;
    private IReadOnlyList<CameraFile>? shown;

    /// <summary>Creates the page.</summary>
    public LibraryViewModel(AppParts parts)
    {
        this.parts = parts ?? throw new ArgumentNullException(nameof(parts));
    }

    /// <summary>The card's files, newest day first.</summary>
    public ObservableCollection<CardItem> Items { get; } = [];

    /// <summary>The page's heading: what the card holds, or why nothing shows.</summary>
    [ObservableProperty]
    private string heading = "No camera connected";

    /// <summary>One sentence under the heading.</summary>
    [ObservableProperty]
    private string detail = "Connect a camera to see what is on its card.";

    /// <summary>Whether a camera is connected.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool connected;

    /// <summary>Whether the camera is free for another command.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(CopyCommand), nameof(DeleteCommand), nameof(ConfirmDeleteCommand), nameof(PlayCommand))]
    private bool idle;

    /// <summary>How many files are picked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CopyLabel), nameof(DeleteQuestion))]
    [NotifyCanExecuteChangedFor(nameof(CopyCommand), nameof(DeleteCommand), nameof(ConfirmDeleteCommand))]
    private int selectedCount;

    /// <summary>Whether the delete confirmation shows.</summary>
    [ObservableProperty]
    private bool confirmingDelete;

    /// <summary>The clip playing over the page; null when none is.</summary>
    [ObservableProperty]
    private PlayerViewModel? player;

    /// <summary>The copy button's label.</summary>
    public string CopyLabel => $"Copy {SelectedCount} to this PC";

    /// <summary>The delete confirmation's question.</summary>
    public string DeleteQuestion => SelectedCount == 1
        ? "Delete this file from the camera?"
        : $"Delete {SelectedCount} files from the camera?";

    /// <summary>Where copies go.</summary>
    public string Folder => parts.Preferences.Current.DownloadFolder ?? FolderSink.DefaultFolder;

    /// <summary>Reads the card the first time the page is opened with a camera connected.</summary>
    public void Opened()
    {
        var state = parts.Controller.State;
        if (state.IsConnected && state.Library.Files is null && !state.Library.Reading)
        {
            _ = parts.Controller.RefreshLibrary();
        }
    }

    /// <summary>Closes the player, which stops fetching its clip if it still is.</summary>
    public void ClosePlayer()
    {
        var closing = Player;
        Player = null;
        closing?.Dispose();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        ClosePlayer();
        foreach (var item in Items)
        {
            item.PropertyChanged -= OnItemChanged;
            item.Dispose();
        }
    }

    /// <summary>Follows the camera and its card.</summary>
    internal void Apply(CameraState state)
    {
        Connected = state.IsConnected;
        Idle = state.Task is null;
        var library = state.Library;
        if (!ReferenceEquals(library.Files, shown))
        {
            Rebuild(library.Files);
        }

        foreach (var item in Items)
        {
            if (library.Thumbnails.TryGetValue(item.File, out var jpeg))
            {
                item.ShowThumbnail(jpeg, parts.Decode);
            }

            item.Transfer = library.Transfers.TryGetValue(item.File, out var transfer) ? CardWords.Transfer(transfer) : null;
        }

        (Heading, Detail) = (Connected, library.Reading, library.Files) switch
        {
            (false, _, _) => ("No camera connected", "Connect a camera to see what is on its card."),
            (_, true, _) => ("Reading the card", "The live picture pauses while the camera lists its files."),
            (_, _, null) => ("The card", "Read the card to see what is on it."),
            (_, _, { Count: 0 }) => ("The card is empty", "The camera says its card is empty, or that it has no card."),
            (_, _, var files) => (CardWords.Count(files), $"Copies go to {Folder}."),
        };
    }

    private void Rebuild(IReadOnlyList<CameraFile>? files)
    {
        foreach (var item in Items)
        {
            item.PropertyChanged -= OnItemChanged;
            item.Dispose();
        }

        Items.Clear();
        shown = files;
        foreach (var (day, onDay) in CardWords.ByDay(files ?? []))
        {
            for (var i = 0; i < onDay.Count; i++)
            {
                var item = new CardItem(onDay[i], day, firstOfDay: i == 0);
                item.PropertyChanged += OnItemChanged;
                Items.Add(item);
            }
        }

        SelectedCount = 0;
        ConfirmingDelete = false;
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CardItem.Selected))
        {
            SelectedCount = Items.Count(item => item.Selected);
        }
    }

    private List<CameraFile> Picked() => Items.Where(item => item.Selected).Select(item => item.File).OrderBy(file => file.Index).ToList();

    private void PickNone()
    {
        foreach (var item in Items)
        {
            item.Selected = false;
        }
    }

    private bool CanRefresh() => Connected && Idle;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private void Refresh() => parts.Controller.RefreshLibrary();

    private bool CanPlay(CardItem? item) => item is { File.IsVideo: true } && Idle;

    /// <summary>Plays a video over the page: from this PC when it was played before, otherwise as it comes off the card.</summary>
    [RelayCommand(CanExecute = nameof(CanPlay))]
    private void Play(CardItem? item)
    {
        if (parts.Controller.Play(item!.File, parts.Clips) is { } clip)
        {
            ClosePlayer();
            Player = new PlayerViewModel(clip, parts, ClosePlayer);
        }
    }

    private bool CanUsePicked() => SelectedCount > 0 && Idle;

    /// <summary>Copies the picked files to this PC, deleting each from the card once safe when the person asked.</summary>
    [RelayCommand(CanExecute = nameof(CanUsePicked))]
    private void Copy()
    {
        parts.Controller.Download(Picked(), new FolderSink(Folder), parts.Preferences.Current.DeleteAfterCopy);
        PickNone();
    }

    [RelayCommand(CanExecute = nameof(CanUsePicked))]
    private void Delete() => ConfirmingDelete = true;

    /// <summary>Deletes the picked files from the card; anything already copied to this PC stays.</summary>
    [RelayCommand(CanExecute = nameof(CanUsePicked))]
    private void ConfirmDelete()
    {
        parts.Controller.Delete(Picked());
        ConfirmingDelete = false;
        PickNone();
    }

    [RelayCommand]
    private void CancelDelete() => ConfirmingDelete = false;

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var item in Items)
        {
            item.Selected = true;
        }
    }

    [RelayCommand]
    private void SelectNone() => PickNone();

    [RelayCommand]
    private void OpenFolder() => parts.Desktop.OpenFolder(Folder);
}
