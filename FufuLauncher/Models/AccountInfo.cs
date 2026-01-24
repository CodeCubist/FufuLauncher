using CommunityToolkit.Mvvm.ComponentModel;

namespace FufuLauncher.Models;

public partial class AccountInfo : ObservableObject
{
    [ObservableProperty] private string _nickname = "";
    [ObservableProperty] private string _gameUid = "";
    [ObservableProperty] private string _server = "";
    [ObservableProperty] private string _avatarUrl = "ms-appx:///Assets/DefaultAvatar.png";
    [ObservableProperty] private string _level = "";
    [ObservableProperty] private string _sign = "这个人很懒，什么都没有写...";
    [ObservableProperty] private string _ipRegion = "未知";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GenderIcon))]
    [NotifyPropertyChangedFor(nameof(GenderText))]
    private int _gender = 0;
    public string GenderIcon => _gender switch
    {
        1 => "\uE13D",
        2 => "\uE13C",
        _ => "\uE77B"
    };

    public string GenderText => _gender switch
    {
        1 => "男",
        2 => "女",
        _ => "保密"
    };
}

public class UserDisplayConfig
{
    public string Nickname { get; set; } = "";
    public string GameUid { get; set; } = "";
    public string Server { get; set; } = "";
    public string AvatarUrl { get; set; } = "ms-appx:///Assets/DefaultAvatar.png";
    public string Level { get; set; } = "";
    public string Sign { get; set; } = "";
    public string IpRegion { get; set; } = "";
    public int Gender { get; set; } = 0;
}