namespace ValorantPorting.Views.Controls;

// Something that can be a favorite / show up in "Recent": skin, agent and buddy tiles, animations and maps.
public interface ILibraryItem
{
    string LibraryId { get; }
    bool IsFavorite { get; set; }
    int RecentRank { get; }
}
