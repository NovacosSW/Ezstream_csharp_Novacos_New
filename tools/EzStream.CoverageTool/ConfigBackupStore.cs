using System.Text;

namespace EzStream.CoverageTool;

internal sealed class ConfigBackupStore
{
    private readonly string _backupPath;

    public ConfigBackupStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _backupPath = Path.Combine(dataDirectory, "pending-config-backup.json");
    }

    public bool HasPendingBackup => File.Exists(_backupPath);

    public void Save(string configJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configJson);
        if (HasPendingBackup)
            throw new InvalidOperationException("복구되지 않은 설정 백업이 있습니다. 먼저 원래 설정 복구를 실행하십시오.");

        File.WriteAllText(_backupPath, configJson, new UTF8Encoding(false));
    }

    public string Load()
    {
        if (!HasPendingBackup)
            throw new InvalidOperationException("복구할 설정 백업이 없습니다.");

        return File.ReadAllText(_backupPath, Encoding.UTF8);
    }

    public void Delete()
    {
        if (HasPendingBackup)
            File.Delete(_backupPath);
    }
}
