# Biblioteka nagrywania z kamer (wrapper MediaMTX) – wymagania

Stan: 2026-11-02 – biblioteka `ITBees.Recordings` zaimplementowana według „Proponowanego kształtu”
(opis działania: README.md). Pytania 1, 6 i 7 rozstrzygnięte w bibliotece; 2–5 dotyczą integracji
z Octoparkiem i czekają na decyzję. Niesprawdzone jeszcze na słupku.

## Wymagania (od Kacpra)
1. Biblioteka NuGet w stylu pozostałych `ITBees.*` (.NET 8), wrapper na MediaMTX.
2. Biblioteka sama ogarnia domyślną konfigurację:
   - permanentne nagrywanie wszystkiego do plików, podział na pliki co ustawiony czas (domyślnie 15 min),
   - wycinki powiązane ze zdarzeniami parkingowymi: domyślnie 45 s przed i 45 s po zdarzeniu (obie wartości ustawialne),
   - parametryzacja jakości nagrania, żeby zarządzać wielkością plików.
3. Dwa procesy:
   - **rejestrator** – długie nagrania (pliki po 15 min),
   - **wycinarka** – wycina zdarzenia ±45 s (lub inny ustawiony zakres).
   Dwa zdarzenia (np. dwa wjazdy) w odstępie krótszym niż 45 s mają wspólny fragment nagrania.
4. Słupek ustawiony jako **serwer lokalny** ma podpięty dysk USB, na który trafiają pliki.
5. Instalacja na słupku razem z naszym oprogramowaniem, od razu – w runtime, nie jako krok builda.

## Ustalone w teście (stanowisko Kacpra, 2026-11-02)
- Słupek 192.168.10.2 (RPi), kamera IPOX PX-TZIP2012IR7LPR-WG 192.168.10.3, fw 5.1.
- ONVIF na porcie 80 (`/onvif/device_service`), usługi: Media, Events, Imaging, PTZ, Analytics, Recording/Search/Replay.
- Konto z zakładki Onvif w panelu kamery działa i dla ONVIF, i dla RTSP. Konto WWW (to, które zna słupek) dla RTSP – niesprawdzone.
- Profile: `profile1` H264 1920×1080 25 fps 3072 kb/s (~1,4 GB/h), `profile2` 704×576 768 kb/s (~0,35 GB/h),
  `profile3` 480×240 512 kb/s (~0,23 GB/h); GOP 100 (klatka kluczowa co 4 s). Snapshot: `http://<ip>/snapshot.JPG`.
- Nagranie `ffmpeg -c copy` z profile1: 10 s = 3,9 MB, H264 1920×1080 25 fps – OK.
- Karta SD słupka: 29 GB, 23 GB wolne.

## Proponowany kształt (do akceptacji)
- **Rejestrator = MediaMTX** jako osobna usługa systemd zakładana przez bibliotekę w runtime (nagrywanie trwa
  przy restarcie/aktualizacji ColumnApp). Konfiguracja kamer w locie przez API MediaMTX
  (`/v3/config/paths/add|patch|delete`, tylko localhost), bez restartu. Format fMP4, `recordPartDuration 1s`
  (po zaniku zasilania traci się najwyżej 1 s), `recordSegmentDuration 15m`.
- **Wycinarka = hosted service w .NET**: kolejka zdarzeń (kamera, czas, id zdarzenia, przed, po). Zadanie rusza,
  gdy minie `czas + po + zapas` (~5 s), i pobiera wycinek z serwera playback MediaMTX
  (`/get?path=…&start=…&duration=…&format=mp4`) – MediaMTX sam skleja fragmenty z kilku plików 15-min, więc
  ffmpeg nie jest potrzebny. Każde zdarzenie dostaje własny plik; wspólny fragment dwóch bliskich zdarzeń jest
  w obu plikach (niezależne kasowanie/wysyłka).
- **Dokładność początku wycinka** = odstęp klatek kluczowych (GOP). Przy GOP 100/25 fps wycinek może zacząć się
  do 4 s wcześniej; zalecany GOP = 2 × fps.
- **Jakość/rozmiar bez przekodowania** (RPi nie udźwignie transkodowania): wybór profilu ONVIF oraz zapis
  parametrów enkodera w kamerze przez ONVIF `SetVideoEncoderConfiguration` (rozdzielczość, bitrate, fps, GOP).
  Rozmiar ≈ bitrate[kb/s] × 0,45 MB/h.
- **Retencja**: MediaMTX kasuje tylko po wieku (`recordDeleteAfter`) – biblioteka musi mieć własnego strażnika
  wolnego miejsca (kasowanie najstarszych segmentów przy progu), bo pełny dysk zatrzymuje ColumnApp.
- **Serwer lokalny z USB**: wykrycie i montowanie dysku (po UUID/etykiecie), nagrania tylko na USB; brak dysku
  ⇒ nie nagrywać na kartę SD (albo tylko mały bufor) i zgłosić alarm.
- **Instalacja w runtime**: biblioteka sprawdza obecność i wersję binarki MediaMTX (`/opt/…`), w razie potrzeby
  ją rozpakowuje, zapisuje konfigurację i jednostkę systemd, uruchamia usługę.

## Otwarte pytania
1. ~~Nazwa biblioteki / repo.~~ `ITBees.Recordings`, github.com/ITBeesPL/ITBees.Recordings.
2. Serwer lokalny: nagrywa wszystkie kamery parkingu (ściąga RTSP z każdej po LAN), czy tylko własne?
   Czy zwykłe słupki (bez USB) nagrywają cokolwiek?
3. Które zdarzenia tworzą wycinki (wjazd, wyjazd, ręczne otwarcie, płatność, zdarzenia podejrzane…) i jak trafiają
   do serwera lokalnego zdarzenia z innych słupków.
4. Co dalej z wycinkiem: tylko lokalnie, wysyłka na img.octopark, podpięcie pod sesję/dziennik (`JournalEventGuid`)?
5. Retencja: osobno dla nagrań ciągłych i wycinków; zachowanie przy pełnym dysku USB.
   Biblioteka: `RecordingRetention` (domyślnie 72 h), `ClipRetention` (30 dni), strażnik `MinFreeSpaceMegabytes`
   (3 GB) kasuje najpierw najstarsze nagrania ciągłe, potem najstarsze wycinki. Wartości dla Octoparka – do ustalenia.
6. ~~Dane do RTSP~~ – biblioteka bierze `Username`/`Password` kamery dla ONVIF i RTSP. Konto ONVIF działa;
   czy konto WWW (to, które słupek już zna) też otwiera RTSP – nadal niesprawdzone.
7. ~~Binarka MediaMTX~~ – pobierana z GitHuba przy pierwszym starcie (SHA-256 sprawdzane), bez internetu:
   `MediaMtxArchivePath` wskazuje archiwum położone na urządzeniu. Paczka NuGet zostaje mała (~170 kB).
