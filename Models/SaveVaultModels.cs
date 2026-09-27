using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SteamLuaManager.Models;

public partial class SaveGameRecord : ObservableObject
{
    public int AppId { get; init; }
    public string GameName { get; init; } = string.Empty;
    public string CoverImagePath { get; init; } = string.Empty;
    public ObservableCollection<SaveLocationInfo> Locations { get; } = new();
    public ObservableCollection<SaveSnapshotInfo> Snapshots { get; } = new();
    public ObservableCollection<SaveSlotPreview> Slots { get; } = new();

    [ObservableProperty] private int _fileCount;
    [ObservableProperty] private long _totalBytes;
    [ObservableProperty] private DateTime? _lastModified;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "尚未扫描";
    [ObservableProperty] private string _operationStatus = string.Empty;

    public bool HasSaveData => Locations.Count > 0 && FileCount > 0;
    public int BackupCount => Snapshots.Count;
    public string SizeText => FormatBytes(TotalBytes);
    public string LastModifiedText => LastModified is null
        ? "暂无存档"
        : LastModified.Value.ToString("yyyy年M月d日 HH:mm");
    public string LocationCountText => Locations.Count == 0 ? "未找到" : $"{Locations.Count} 个文件夹";
    public string BackupCountText => $"{BackupCount} 个备份";
    public string SlotCountText => Slots.Count == 0 ? "暂无槽位" : $"{Slots.Count} 个槽位";

    public void NotifySummaryChanged()
    {
        OnPropertyChanged(nameof(HasSaveData));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(LastModifiedText));
        OnPropertyChanged(nameof(LocationCountText));
        OnPropertyChanged(nameof(BackupCount));
        OnPropertyChanged(nameof(BackupCountText));
        OnPropertyChanged(nameof(SlotCountText));
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024L * 1024L) return $"{bytes / (1024d * 1024d * 1024d):0.00} GB";
        if (bytes >= 1024L * 1024L) return $"{bytes / (1024d * 1024d):0.00} MB";
        if (bytes >= 1024L) return $"{bytes / 1024d:0.0} KB";
        return $"{bytes} B";
    }
}

public sealed record SaveLocationInfo(
    string Path,
    string Source,
    int FileCount,
    long TotalBytes,
    DateTime LastModified)
{
    public string SizeText => FormatBytes(TotalBytes);
    public string SummaryText => $"{FileCount} 个文件 · {SizeText} · {LastModified:yyyy年M月d日 HH:mm}";

    private static string FormatBytes(long bytes) => bytes >= 1024L * 1024L
        ? $"{bytes / (1024d * 1024d):0.00} MB"
        : bytes >= 1024L ? $"{bytes / 1024d:0.0} KB" : $"{bytes} B";
}

public sealed record SaveSnapshotInfo(
    string FilePath,
    DateTime CreatedAt,
    long SizeBytes,
    int FileCount,
    string Reason)
{
    public string CreatedText => CreatedAt.ToString("yyyy年M月d日 HH:mm:ss");
    public string SizeText => SizeBytes >= 1024L * 1024L
        ? $"{SizeBytes / (1024d * 1024d):0.00} MB"
        : SizeBytes >= 1024L ? $"{SizeBytes / 1024d:0.0} KB" : $"{SizeBytes} B";
    public string DetailText => $"{Reason} · {FileCount} 个文件 · {SizeText}";
}

public sealed record SaveOperationResult(bool Success, string Message, SaveSnapshotInfo? Snapshot = null);

public sealed class SaveSlotPreview
{
    public string FileName { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string Format { get; init; } = string.Empty;
    public DateTime LastModified { get; init; }
    public long SizeBytes { get; init; }
    public IReadOnlyList<SaveDataField> Fields { get; init; } = [];
    public bool HasStructuredData => Fields.Count > 0;
    public string StatusText => HasStructuredData ? "已读取" : "仅显示文件信息";
    public string MetaText => $"{Format} · {FormatBytes(SizeBytes)} · {LastModified:yyyy年M月d日 HH:mm}";

    private static string FormatBytes(long bytes) => bytes >= 1024L * 1024L
        ? $"{bytes / (1024d * 1024d):0.00} MB"
        : bytes >= 1024L ? $"{bytes / 1024d:0.0} KB" : $"{bytes} B";
}

public sealed record SaveDataField(string Label, string Value);
