namespace EzStream.Core;

/// <summary>설치/실행 공통 경로. 서비스와 트레이가 동일 값을 사용해야 한다.</summary>
public static class AppPaths
{
    /// <summary>%ProgramData%\EzStream</summary>
    public static string DataDir
    {
        get
        {
            var pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(pd, "EzStream");
        }
    }

    /// <summary>설정 파일 경로. 서비스가 소유·저장한다.</summary>
    public static string ConfigPath => Path.Combine(DataDir, "config.json");

    /// <summary>로그 디렉토리.</summary>
    public static string LogDir => Path.Combine(DataDir, "logs");

    /// <summary>실행 파일이 위치한 폴더(설치 폴더).</summary>
    public static string InstallDir => AppContext.BaseDirectory;

    /// <summary>번들 FFmpeg 네이티브 DLL 폴더. 설치 폴더\ffmpeg. 없으면 실행 폴더 자체.</summary>
    public static string FfmpegDir
    {
        get
        {
            var sub = Path.Combine(InstallDir, "ffmpeg");
            return Directory.Exists(sub) ? sub : InstallDir;
        }
    }

    /// <summary>서비스 ↔ 트레이 통신용 Named Pipe 이름.</summary>
    public const string PipeName = "ezstream-recorder";
}
