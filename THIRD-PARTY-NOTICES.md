# Licencje bibliotek zewnętrznych

GameTranslatorOverlay korzysta z poniższych bibliotek open source. Pełne teksty licencji
i noty o prawach autorskich są w folderze `licenses` w paczce z programem (w repozytorium:
`src/GameTranslatorOverlay.App/ThirdPartyLicenses`): `Packages.txt` (każdy pakiet z wersją,
licencją i notą), `MIT.txt`, `Apache-2.0.txt`, `SQLite-PublicDomain.txt` oraz noty platformy .NET
(`DotNet-Runtime-LICENSE.txt`, `DotNet-Runtime-THIRD-PARTY-NOTICES.txt`).

## Aplikacja

| Biblioteka | Wersja | Licencja | Zastosowanie |
|---|---|---|---|
| .NET Runtime i WPF (Microsoft; dołączone do paczki) | 10.0.12 | MIT | platforma aplikacji |
| Projekcje Windows SDK (`Microsoft.Windows.SDK.NET.Ref`) | 10.0.19041.57 | licencja Microsoft Windows SDK | dostęp do Windows OCR i systemowych API |
| Microsoft.Extensions.Hosting | 10.0.10 | MIT | wstrzykiwanie zależności, host aplikacji |
| Microsoft.Extensions.Logging.Abstractions | 10.0.10 | MIT | logowanie |
| Microsoft.Extensions.* (Configuration, DependencyInjection, Diagnostics, FileProviders, Logging, Options, Primitives — zależności Hosting) | 10.0.10 | MIT | konfiguracja, opcje, dostawcy logów |
| Microsoft.Data.Sqlite (+ Microsoft.Data.Sqlite.Core) | 10.0.10 | MIT | dostęp do bazy SQLite (cache tłumaczeń) |
| SQLitePCLRaw.bundle_e_sqlite3 (+ config, core, provider) | 3.0.5 | Apache-2.0 | natywny silnik SQLite |
| SQLite (`SQLite`, SourceGear) | 3.53.4 | Public Domain | silnik bazy danych |
| System.Security.Cryptography.ProtectedData (Microsoft) | 10.0.14 | MIT | szyfrowanie klucza API (DPAPI) |
| System.IO.Hashing (Microsoft) | 10.0.10 | MIT | szybki skrót XxHash128 odcisku regionu tekstu w trybie live |
| Serilog | 4.3.0 | Apache-2.0 | logi diagnostyczne |
| Serilog.Extensions.Hosting | 10.0.0 | Apache-2.0 | logi diagnostyczne w hoście aplikacji |
| Serilog.Extensions.Logging | 10.0.0 | Apache-2.0 | most między Serilog a Microsoft.Extensions.Logging |
| Serilog.Sinks.File | 7.0.0 | Apache-2.0 | zapis logów do pliku |
| H.NotifyIcon.Wpf (+ H.NotifyIcon, H.GeneratedIcons.System.Drawing) | 2.4.1 | MIT | ikona w zasobniku systemowym |
| Anthropic (oficjalne SDK C#) | 12.51.0 | MIT | opcjonalny dostawca tłumaczeń Claude |
| Microsoft.Extensions.AI.Abstractions | 10.5.1 | MIT | abstrakcje używane przez SDK Anthropic |

## Czcionki dołączone do aplikacji

| Czcionka | Licencja | Zastosowanie |
|---|---|---|
| Lexend Deca (Regular, Medium, SemiBold, Bold; wersja 1.007) — Copyright 2019 The Lexend Project Authors (https://github.com/googlefonts/lexend) | SIL Open Font License 1.1 | krój napisów nakładki w profilu Escape Academy (`overlay.fontFamily`) |
| Zilla Slab (Bold; wersja 1.1) — Copyright 2017, The Mozilla Foundation (https://github.com/mozilla/zilla-slab, plik z https://github.com/google/fonts) | SIL Open Font License 1.1 | szeryfowy krój żółtego napisu z obrysem (imię mówiącego) w profilu Escape Academy (`overlay.fontRules`) |

Pliki czcionek są zasobami aplikacji (`src/GameTranslatorOverlay.App/Fonts`), niezmienione.
Pełne teksty licencji obok programu: `licenses/LexendDeca-OFL.txt`
(`src/GameTranslatorOverlay.App/Fonts/OFL.txt` w repozytorium) i `licenses/ZillaSlab-OFL.txt`
(`src/GameTranslatorOverlay.App/Fonts/ZillaSlab-OFL.txt`). Czcionki nie są sprzedawane
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
