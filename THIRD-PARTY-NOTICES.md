# Licencje bibliotek zewnętrznych

GameTranslatorOverlay korzysta z poniższych bibliotek open source. Pełne teksty licencji
znajdują się na stronach projektów.

## Aplikacja

| Biblioteka | Licencja | Zastosowanie |
|---|---|---|
| .NET Runtime / WPF (Microsoft) | MIT | platforma aplikacji |
| Microsoft.Extensions.Hosting / Logging | MIT | wstrzykiwanie zależności, logowanie |
| Microsoft.Data.Sqlite | MIT | dostęp do bazy SQLite (cache tłumaczeń) |
| SQLitePCLRaw.bundle_e_sqlite3 | Apache-2.0 | natywny silnik SQLite |
| SQLite | Public Domain | silnik bazy danych |
| System.Security.Cryptography.ProtectedData | MIT | szyfrowanie klucza API (DPAPI) |
| Serilog + Serilog.Extensions.Hosting + Serilog.Sinks.File | Apache-2.0 | logi diagnostyczne |
| H.NotifyIcon.Wpf | MIT | ikona w zasobniku systemowym |

## Wyłącznie do budowania i testów (nie są dystrybuowane z aplikacją)

| Biblioteka | Licencja |
|---|---|
| xunit / xunit.runner.visualstudio | Apache-2.0 |
| Microsoft.NET.Test.Sdk | MIT |
| coverlet.collector | MIT |

Usługi zewnętrzne: tłumaczenia wykonuje **DeepL API** zgodnie z regulaminem DeepL
(https://www.deepl.com/pro-license) — wymaga własnego klucza użytkownika. Systemowe OCR
to wbudowany komponent Windows (Windows.Media.Ocr).
