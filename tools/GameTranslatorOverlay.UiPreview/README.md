# UiPreview

Narzędzie deweloperskie do oglądania okna konfiguracji. Okno aplikacji jest wykluczone z przechwytywania ekranu (`SetWindowDisplayAffinity`), więc zwykły zrzut ekranu go nie pokaże — UiPreview renderuje je przez `RenderTargetBitmap`.

```
dotnet build GameTranslatorOverlay.slnx -c Release
tools/GameTranslatorOverlay.UiPreview/bin/Release/net10.0-windows10.0.19041.0/GameTranslatorOverlay.UiPreview.exe --out C:\folder\na\obrazki
tools/GameTranslatorOverlay.UiPreview/bin/Release/net10.0-windows10.0.19041.0/GameTranslatorOverlay.UiPreview.exe --write-icon src/GameTranslatorOverlay.App/app.ico
```

Co robi:

- tworzy prawdziwe `MainWindow` poza ekranem, na tymczasowym folderze danych (usuwanym na końcu), z dostawcą Mock, w trybie prywatnym, z `DisclaimerAcknowledged = true`, bez sieci (każde żądanie HTTP rzuca wyjątek) i z kluczami tylko w pamięci;
- rejestruje skróty globalne na nietypowych kombinacjach (Ctrl+Alt+Shift+F21–F23), żeby nie zająć skrótów działającej kopii aplikacji; w oknie pokazuje domyślne nazwy skrótów;
- podstawia syntetyczną listę okien, ostatni wynik, statystyki i podgląd (bez tytułów prawdziwych okien i bez treści z gier);
- sprawdza, czy wybór w kartach i przełącznikach zapisuje te same wartości ustawień co dawne listy (`SelectedIndex` → `OverlayPlacement`, `LiveDisplayMode`, tło, dostawca…);
- przełącza po kolei sekcje nawigacji (albo renderuje całe okno, gdy nawigacji nie ma) i zapisuje PNG w skali 1.0 i 1.5 przy rozmiarze domyślnym, minimalnym oraz rozmiarze obszaru roboczego, a przy rozmiarze domyślnym także całą przewijaną stronę;
- na chwilę maksymalizuje okno i porównuje położenie treści z obszarem roboczym monitora (wcięcie przy `WindowChrome`);
- dodatkowo: warianty dostawcy (DeepL, Claude, serwer LLM), stany trybu live (działa, wstrzymany), brak wybranej gry i brak podglądu, rozwinięte listy, podpowiedź, menu zasobnika, ikony okna i zasobnika, edytor słownika i panel wyniku nad syntetyczną sceną;
- w `raport.txt` zapisuje środowisko (DPI, czcionki), napisy ucięte albo skrócone wielokropkiem, kontrolki zasłonięte dla myszy (test trafienia w środek kontrolki), wynik maksymalizacji i glify ikon, których brakuje w Segoe MDL2 Assets (zapas dla Windows 10).

`--write-icon` zapisuje znak aplikacji (`Ui/AppLogo`) jako `.ico` (16–64 px w DIB, 256 px w PNG); tak powstał `src/GameTranslatorOverlay.App/app.ico`.

Skala 1.5 to rendering w 144 DPI; układ liczy się w DPI monitora, na którym system umieścił okno poza ekranem. Stany najechania myszą i fokusu klawiatury nie są renderowane. Menu zasobnika, rozwinięte listy, podpowiedź i maksymalizacja pokazują się na chwilę na ekranie (WPF nie pozwala wyświetlić wyskakującego okna poza ekranem). Ikona w zasobniku pojawia się na czas działania narzędzia i znika przy zamknięciu okna.
