# KSeF Monitor

Desktopowa aplikacja Windows 11 do monitorowania faktur otrzymanych w Krajowym Systemie e-Faktur (KSeF API 2.0).

Aktualny etap: działający prototyp `0.1.0`. Projekt kompiluje się bez ostrzeżeń, a klient API i parser XML mają testy dymne. Przed użyciem produkcyjnym należy przeprowadzić testy integracyjne na środowiskach TEST i DEMO.

## Zaimplementowane

- logowanie tokenem KSeF w kontekście NIP;
- pobieranie aktualnego klucza publicznego i obsługa `publicKeyId`;
- RSA-OAEP/SHA-256 dla `token|timestampMs`;
- wymiana `AuthenticationToken` na access/refresh token oraz automatyczne odświeżanie;
- przyrostowe zapytania `POST /invoices/query/metadata` dla `Subject2`;
- synchronizacja po `PermanentStorage`, sortowanie rosnące, HWM, paginacja i deduplikacja po numerze KSeF;
- automatyczne odświeżanie co 15 minut;
- lista bieżącego i poprzedniego miesiąca;
- trwały status `NOWA`, usuwany po otwarciu faktury;
- pobieranie XML nowych dokumentów z tempem zgodnym z limitem API;
- okno szczegółów: metadane, pozycje z ilością, jednostką, cenami i wartościami netto/brutto, VAT oraz rabatem, wszystkie pola i surowy XML;
- odczyt pozycji z FA(3), alternatywnych pól brutto oraz dokumentów PEF/UBL;
- minimalizacja do traya i powiadomienia o nowych fakturach;
- szyfrowanie tokena i lokalnego cache za pomocą Windows DPAPI (`CurrentUser`);
- tryb offline dla już zsynchronizowanych danych;
- pojedyncza instancja aplikacji.

## Wymagania deweloperskie

- Windows 11 x64;
- .NET SDK 10.0.302 lub nowszy zgodny z `global.json`;
- do testów integracyjnych: NIP i token KSeF z uprawnieniem `InvoiceRead`.

Projekt nie wymaga zewnętrznych pakietów NuGet. Integracja używa kontraktu KSeF API bezpośrednio i standardowych bibliotek .NET.

## Uruchomienie deweloperskie

```powershell
dotnet run --project .\src\KsefMonitor\KsefMonitor.csproj
```

Przy pierwszym uruchomieniu:

1. Wybierz `TEST`.
2. Wprowadź testowy NIP i token KSeF.
3. Kliknij „Sprawdź połączenie”.
4. Zapisz ustawienia.

Nie używaj rzeczywistych danych ani produkcyjnego tokena w środowisku TEST.

## Testy

```powershell
dotnet run --project .\tests\KsefMonitor.SmokeTests\KsefMonitor.SmokeTests.csproj
```

Test obejmuje:

- walidację NIP;
- parser przykładowej faktury FA(3), wariantu wartości brutto oraz PEF/UBL;
- generowanie i odszyfrowanie żądania RSA-OAEP;
- cały przebieg challenge → auth → status → redeem;
- sprawdzenie filtra `Subject2` i `PermanentStorage`;
- mapowanie metadanych faktury.

## Publikacja pojedynczego pliku EXE

Na Windows uruchom wariant `.cmd`, który nie wymaga zezwolenia na wykonywanie skryptów PowerShell:

```powershell
.\scripts\publish-win-x64.cmd
```

Alternatywnie, jeżeli wykonywanie skryptów PowerShell jest dozwolone:

```powershell
.\scripts\publish-win-x64.ps1
```

Wynik:

```text
artifacts\win-x64\KSeFMonitor.exe
```

Jest to samodzielny plik `win-x64`, zawierający runtime .NET. Aplikacja tworzy dane użytkownika w:

```text
%LOCALAPPDATA%\KSeF Monitor
```

- `settings.json` — niesekretne ustawienia;
- `credential.dat` — token chroniony DPAPI;
- `invoices.dat` — metadane, statusy i XML-e chronione DPAPI.

Plików `.dat` nie da się odszyfrować na innym koncie Windows. Usunięcie folderu resetuje aplikację i lokalny cache.

## Zachowanie synchronizacji

- pierwsza synchronizacja zaczyna się od początku poprzedniego miesiąca;
- kolejne używają ostatniego HWM z jednosekundowym nakładaniem zakresu;
- aplikacja prowadzi trwały licznik pobrań XML i wykonuje najwyżej 60 w dowolnym oknie godzinnym;
- ręczne odświeżenie ma pięciominutowy cooldown, a poprawna synchronizacja ustawia następny cykl za 15 minut;
- przy błędzie sieci aplikacja zachowuje cache i ponawia próbę po pięciu minutach;
- HTTP 429 nie jest maskowany — komunikat zawiera `Retry-After` zwrócone przez KSeF.

## Ważne przed produkcją

1. Zweryfikować działanie z aktualnym środowiskiem TEST.
2. Przeprowadzić test na DEMO z profilem limitów produkcyjnych.
3. Podpisać `KSeFMonitor.exe` certyfikatem code-signing, aby ograniczyć ostrzeżenia SmartScreen.
4. Dodać automatyczny proces sprawdzania zmian OpenAPI KSeF przed każdym wydaniem.
5. Przy organizacjach odbierających więcej niż 60 nowych faktur na godzinę rozszerzyć pobieranie XML o `/invoices/exports`; obecna wersja bezpiecznie kolejkuje nadmiar na następne cykle.

## Źródła kontraktu

- [KSeF API produkcyjne](https://api.ksef.mf.gov.pl/docs/v2/index.html)
- [Oficjalny przewodnik integratora](https://github.com/CIRFMF/ksef-api)
- [Pobieranie faktur](https://github.com/CIRFMF/ksef-api/blob/main/pobieranie-faktur/pobieranie-faktur.md)
- [Synchronizacja przyrostowa](https://github.com/CIRFMF/ksef-api/blob/main/pobieranie-faktur/przyrostowe-pobieranie-faktur.md)
- [Limity API](https://github.com/CIRFMF/ksef-api/blob/main/limity/limity-api.md)
