using System.Collections.Generic;
using Android.App;
using Android.Content.PM;
using Android.OS;
using Avalonia;
using Avalonia.Android;

namespace LocalBridge.Android;

[Activity(
    Label = "LocalBridge",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
    private const int StoragePermissionsRequestCode = 1000;

    // Сетевые разрешения (INTERNET, ACCESS_NETWORK_STATE, ACCESS_WIFI_STATE,
    // CHANGE_WIFI_MULTICAST_STATE) относятся к normal-уровню и выдаются
    // автоматически при установке — их запрашивать в рантайме не нужно.
    //
    // Разрешения хранилища — dangerous, их запрашиваем в рантайме по версии Android:
    //   API 33+  → READ_MEDIA_* (доступ к медиафайлам других приложений)
    //   API 29–32→ READ_EXTERNAL_STORAGE
    //   API ≤ 28 → WRITE_EXTERNAL_STORAGE (запись в публичный /Download/LocalBridge)
    private const string ReadMediaImages = "android.permission.READ_MEDIA_IMAGES";
    private const string ReadMediaVideo = "android.permission.READ_MEDIA_VIDEO";
    private const string ReadMediaAudio = "android.permission.READ_MEDIA_AUDIO";

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        RequestRuntimePermissions();
    }

    private void RequestRuntimePermissions()
    {
        var sdk = (int)Build.VERSION.SdkInt;
        var needed = new List<string>();

        if (sdk <= 28)
        {
            if (CheckSelfPermission(Manifest.Permission.WriteExternalStorage) != Permission.Granted)
                needed.Add(Manifest.Permission.WriteExternalStorage);
        }
        else if (sdk <= 32)
        {
            if (CheckSelfPermission(Manifest.Permission.ReadExternalStorage) != Permission.Granted)
                needed.Add(Manifest.Permission.ReadExternalStorage);
        }
        else
        {
            if (CheckSelfPermission(ReadMediaImages) != Permission.Granted)
                needed.Add(ReadMediaImages);
            if (CheckSelfPermission(ReadMediaVideo) != Permission.Granted)
                needed.Add(ReadMediaVideo);
            if (CheckSelfPermission(ReadMediaAudio) != Permission.Granted)
                needed.Add(ReadMediaAudio);
        }

        if (needed.Count > 0)
            RequestPermissions(needed.ToArray(), StoragePermissionsRequestCode);
    }

    public override void OnRequestPermissionsResult(
        int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);

        // Поведение приложения не зависит от ответа пользователя:
        // на API 29+ запись в собственный каталог Download/LocalBridge работает
        // и без разрешений (scoped storage); на старых версиях отказ просто
        // ограничит приём файлов — пользователь увидит ошибку в статусной строке.
    }
}