namespace SCSKiller.Ui.Pages;

/// <summary>A page that takes the navigation's argument (a game page: the game's id).</summary>
public interface INavigable
{
    void NavigatedTo(object? arg);
}
