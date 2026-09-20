using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using System;

namespace VK_UI3.Helpers
{
    /// <summary>
    /// Сервис системных Windows-уведомлений (Action Center) о начале стрима на Twitch.
    /// Работает как в packaged (MSIX), так и в unpacked конфигурации:
    /// - packaged: identity уже есть в манифесте пакета, Register() работает сразу;
    /// - unpacked: требуется app.manifest c maxversiontested и регистрация через Register().
    /// Если системные уведомления недоступны — методы безопасно возвращают false,
    /// а каналом доставки остаётся баллон в трее / внутренний Flyout.
    /// </summary>
    public static class TwitchWindowsNotificationService
    {
        private const string AppId = "FDW.MusicM";
        private static bool _initialized;

        /// <summary>
        /// Признак того, что приложение упаковано в MSIX (packaged).
        /// В unpacked контексте Package.Current недоступен и выбрасывает исключение.
        /// </summary>
        public static bool IsPackaged
        {
            get
            {
                try
                {
                    _ = Windows.ApplicationModel.Package.Current;
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Инициализирует системные уведомления: регистрирует менеджер и обработчик клика.
        /// Безопасен для вызова несколько раз и при отсутствии поддержки (unpacked без
        /// корректной регистрации) — ошибки перехватываются.
        /// </summary>
        public static bool Initialize(Action<string> onNotificationActivated)
        {
            if (_initialized)
                return true;

            try
            {
                var manager = AppNotificationManager.Default;
                manager.NotificationInvoked += (sender, args) =>
                {
                    // Извлекаем логин стримера из аргументов уведомления (если есть)
                    // и вызываем обработчик в UI-потоке.
                    try
                    {
                        string login = ExtractLogin(args);
                        onNotificationActivated?.Invoke(login);
                    }
                    catch
                    {
                    }
                };

                manager.Register();

                // Регистрация прошла успешно — уведомления доступны.
                _initialized = true;
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Системные уведомления недоступны: {ex.Message}");
                _initialized = false;
                return false;
            }
        }

        /// <summary>
        /// Извлекает логин стримера из аргументов активированного уведомления.
        /// Argument может проецироваться как словарь либо как строка формата
        /// "key=value;key2=value2" — обрабатываем оба варианта безопасно.
        /// </summary>
        private static string ExtractLogin(AppNotificationActivatedEventArgs args)
        {
            object argObj = args?.Argument;
            if (argObj == null)
                return null;

            try
            {
                const string key = "twitchLogin";

                if (argObj is System.Collections.IDictionary dict)
                {
                    if (dict.Contains(key))
                        return dict[key]?.ToString();
                    return null;
                }

                if (argObj is string raw)
                    return ParseArgument(raw, key);

                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Достаёт значение параметра из строки вида "key=value;key2=value2".
        /// </summary>
        private static string ParseArgument(string raw, string key)
        {
            if (string.IsNullOrEmpty(raw))
                return null;

            foreach (var pair in raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int idx = pair.IndexOf('=');
                if (idx > 0 && pair.Substring(0, idx).Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                    return pair.Substring(idx + 1).Trim();
            }

            return null;
        }

        /// <summary>
        /// Показывает системное уведомление о начале стрима. Без звука (режим Silent).
        /// При клике по уведомлению вызывается действие, переданное в Initialize.
        /// </summary>
        public static bool Show(string title, string text, string twitchLogin = null)
        {
            if (!_initialized)
                return false;

            try
            {
                var builder = new AppNotificationBuilder()
                    .AddArgument("action", "open_twitch");

                if (!string.IsNullOrWhiteSpace(twitchLogin))
                    builder.AddArgument("twitchLogin", twitchLogin);

                builder
                    .AddText(title)
                    .AddText(text);

                // Обычный сценарий, без акцентов предупреждения/напоминания.
                builder.SetScenario(AppNotificationScenario.Default);

                var notification = builder.BuildNotification();
                AppNotificationManager.Default.Show(notification);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка показа системного уведомления: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Очищает регистрацию менеджера (при завершении работы приложения).
        /// </summary>
        public static void Uninitialize()
        {
            if (!_initialized)
                return;

            try
            {
                AppNotificationManager.Default.Unregister();
            }
            catch
            {
            }
            finally
            {
                _initialized = false;
            }
        }
    }
}