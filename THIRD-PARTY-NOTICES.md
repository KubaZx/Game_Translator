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
| System.IO.Hashing (Microsoft) | MIT | szybki skrót XxHash128 odcisku regionu tekstu w trybie live |
| Serilog + Serilog.Extensions.Hosting + Serilog.Sinks.File | Apache-2.0 | logi diagnostyczne |
| H.NotifyIcon.Wpf | MIT | ikona w zasobniku systemowym |
| Anthropic (oficjalne SDK C#) + Microsoft.Extensions.AI.Abstractions | MIT | opcjonalny dostawca tłumaczeń Claude |

## Czcionki dołączone do aplikacji

| Czcionka | Licencja | Zastosowanie |
|---|---|---|
| Lexend Deca (Regular, Medium, SemiBold, Bold; wersja 1.007) — Copyright 2019 The Lexend Project Authors (https://github.com/googlefonts/lexend) | SIL Open Font License 1.1 | krój napisów nakładki w profilu Escape Academy (`overlay.fontFamily`) |

Pliki czcionki są zasobami aplikacji (`src/GameTranslatorOverlay.App/Fonts`), niezmienione.
Pełny tekst licencji: `licenses/LexendDeca-OFL.txt` obok programu
(`src/GameTranslatorOverlay.App/Fonts/OFL.txt` w repozytorium). Czcionki nie są sprzedawane
osobno, zgodnie z warunkiem 1 licencji OFL.

## Wyłącznie do budowania i testów (nie są dystrybuowane z aplikacją)

| Biblioteka | Licencja |
|---|---|
| xunit / xunit.runner.visualstudio | Apache-2.0 |
| Microsoft.NET.Test.Sdk | MIT |
| coverlet.collector | MIT |
| BenchmarkDotNet (tylko projekt `benchmarks/`) | MIT |
| ReportGenerator (`dotnet-reportgenerator-globaltool`, lokalne narzędzie do raportu pokrycia) | Apache-2.0 |

Usługi zewnętrzne: tłumaczenia wykonuje wybrany przez użytkownika dostawca — **DeepL API**
(https://www.deepl.com/pro-license), **Azure AI Translator**, **Google Cloud Translation**,
**Anthropic (Claude)** albo serwer zgodny z API OpenAI (np. OpenAI, OpenRouter, lokalna
Ollama / LM Studio) — zgodnie z regulaminem danego dostawcy i z własnym kluczem użytkownika.
Systemowe OCR to wbudowany komponent Windows (Windows.Media.Ocr).
