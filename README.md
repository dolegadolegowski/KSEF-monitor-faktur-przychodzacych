# KSeF Monitor

Desktopowa aplikacja Windows 11 do monitorowania faktur otrzymanych w Krajowym Systemie e-Faktur (KSeF API 2.0).

Aktualny etap: działająca wersja `0.4.6`. Aplikacja łączy się wyłącznie z produkcyjnym API KSeF (`https://api.ksef.mf.gov.pl/v2/`) i nie udostępnia wyboru środowiska.

## Zaimplementowane

- logowanie tokenem KSeF w kontekście NIP;
- pobieranie aktualnego klucza publicznego i obsługa `publicKeyId`;
- RSA-OAEP/SHA-256 dla `token|timestampMs`;
- wymiana `AuthenticationToken` na access/refresh token oraz automatyczne odświeżanie;
- przyrostowe zapytania `POST /invoices/query/metadata` dla `Subject2`;
- synchronizacja po `PermanentStorage`, sortowanie rosnące, HWM, paginacja i deduplikacja po numerze KSeF;
- automatyczne odświeżanie co 15 minut oraz przycisk ręcznego odświeżania z sekundowym odliczaniem;
- lista bieżącego miesiąca i trzech poprzednich miesięcy;
- miesięczne podsumowanie kwot brutto, z oddzielnym wynikiem dla każdej waluty;
- trwały status `NOWA` i zielone podświetlenie całego wiersza, usuwane po pierwszym poprawnym wyświetleniu faktury;
- pobieranie XML nowych dokumentów z tempem zgodnym z limitem API;
- priorytetowe uzupełnianie brakującego XML przy otwieraniu faktury oraz automatyczne odświeżenie podglądu po pobraniu;
- wielostronicowy podgląd dokumentu w proporcjach A4 z danymi stron, tabelą pozycji, płatnością i podsumowaniem;
- okno szczegółów: metadane, pozycje z ilością, jednostką, cenami i wartościami netto/brutto, VAT oraz rabatem, wszystkie pola i surowy XML;
- odczyt pozycji z FA(3), alternatywnych pól brutto oraz dokumentów PEF/UBL;
- minimalizacja do traya z osadzoną, wielorozdzielczą ikoną `KSEF` i powiadomienia o nowych fakturach;
- szyfrowanie tokena i lokalnego cache za pomocą Windows DPAPI (`CurrentUser`);
- izolacja cache i punktu HWM dla konkretnego NIP-u; zmiana kontekstu bezpiecznie zeruje dane poprzedniej firmy;
- atomowy zapis danych z automatyczną kopią `.bak` i próbą odzyskania po uszkodzeniu pliku;
- respektowanie `Retry-After` oraz przerwanie kolejki pobierania XML po odpowiedzi HTTP 429;
- parsowanie dużych XML-i poza wątkiem interfejsu i leniwe ładowanie ciężkich zakładek;
- proste komunikaty błędów na dolnej belce, wyróżnione na czerwono i automatycznie ukrywane po 30 sekundach;
- zakładka `Dziennik` w ustawieniach z technicznym logiem diagnostycznym, bez tokena i treści XML faktur;
- tryb offline dla już zsynchronizowanych danych;
- pojedyncza instancja aplikacji; ponowne uruchomienie przywraca istniejące okno z traya.

## Wymagania deweloperskie

- Windows 11 x64;
- .NET SDK 10.0.302 lub nowszy zgodny z `global.json`;
- do testów integracyjnych: NIP i token KSeF z uprawnieniem `InvoiceRead`.

Projekt nie wymaga zewnętrznych pakietów NuGet. Integracja używa kontraktu KSeF API bezpośrednio i standardowych bibliotek .NET.

## Uruchomienie deweloperskie

```powershell
dotnet run --project .\src\KsefMonitor\KsefMonitor.csproj
```

Przy pierwszym uruchomieniu, już dla produkcyjnego KSeF:

1. Wprowadź rzeczywisty NIP firmy.
2. Wprowadź token wygenerowany w produkcyjnym KSeF z uprawnieniem `InvoiceRead`.
3. Kliknij „Sprawdź połączenie” — aplikacja zweryfikuje logowanie i rzeczywiste uprawnienie `InvoiceRead`.
4. Zapisz ustawienia.

Po aktualizacji ze starszej wersji token zapisany dla TEST/DEMO nie zostanie automatycznie użyty w produkcji — aplikacja poprosi o nowy token produkcyjny.

## Testy

```powershell
dotnet run --project .\tests\KsefMonitor.SmokeTests\KsefMonitor.SmokeTests.csproj
```

Test obejmuje:

- walidację NIP;
- parser przykładowej faktury FA(3), wariantu wartości brutto oraz PEF/UBL;
- algorytm podziału długiej faktury na strony A4 bez utraty lub zmiany kolejności pozycji;
- miesięczne sumowanie kwot brutto i rozdzielanie różnych walut;
- generowanie i odszyfrowanie żądania RSA-OAEP;
- cały przebieg challenge → auth → status → redeem;
- sprawdzenie filtra `Subject2` i `PermanentStorage`;
- mapowanie metadanych faktury;
- pobieranie pełnego XML po numerze KSeF i zachowanie statusu `NOWA` w migawkach listy;
- wymuszenie produkcyjnego adresu API.

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
- `invoices.dat` — metadane, statusy i XML-e chronione DPAPI;
- `download-rate.dat` — mały, chroniony DPAPI licznik limitu pobierania XML;
- `app.log` oraz `app.previous.log` — rotowany dziennik diagnostyczny aplikacji (maksymalnie około 2 MB łącznie).

Przy kolejnych zapisach aplikacja zachowuje poprzednią poprawną wersję każdego pliku jako `.bak`. Kopia jest używana automatycznie, gdy podstawowy plik jest uszkodzony lub niekompletny.

Plików `.dat` nie da się odszyfrować na innym koncie Windows. Usunięcie folderu resetuje aplikację i lokalny cache.

## Zachowanie synchronizacji

- pierwsza synchronizacja zaczyna się od początku trzeciego poprzedniego miesiąca i automatycznie dzieli zakres na przylegające, dwumiesięczne okna zgodne z limitem API;
- aktualizacja z wersji o krótszym zakresie automatycznie cofa HWM i jednorazowo uzupełnia brakującą historię;
- dokumenty pobrane wyłącznie przez takie uzupełnienie historii nie wywołują zaległych oznaczeń `NOWA` ani powiadomień;
- kolejne używają dokładnej wartości ostatniego HWM; błąd `21183` uruchamia bezpieczną odbudowę widocznego zakresu z deduplikacją po numerze KSeF;
- aplikacja prowadzi trwały licznik pobrań XML i wykonuje najwyżej 60 w dowolnym oknie godzinnym;
- ręczne odświeżenie można wymusić przyciskiem w dowolnym momencie, o ile inna synchronizacja właśnie nie trwa;
- po poprawnej synchronizacji licznik rozpoczyna kolejne 15 minut;
- przy błędzie sieci aplikacja zachowuje cache i ponawia próbę po 15 minutach;
- odpowiedzi Problem Details pokazują właściwy kod i szczegóły KSeF zamiast samego ogólnego opisu;
- HTTP 429 nie jest maskowany — komunikat zawiera `Retry-After` zwrócone przez KSeF.

## Ważne przed wdrożeniem

1. Podpisać `KSeFMonitor.exe` certyfikatem code-signing, aby ograniczyć ostrzeżenia SmartScreen.
2. Zweryfikować token na kontrolowanym koncie produkcyjnym z minimalnym uprawnieniem `InvoiceRead`.
3. Dodać automatyczny proces sprawdzania zmian OpenAPI KSeF przed każdym wydaniem.
4. Przy organizacjach odbierających więcej niż 60 nowych faktur na godzinę rozszerzyć pobieranie XML o `/invoices/exports`; obecna wersja bezpiecznie kolejkuje nadmiar na następne cykle.

## Źródła kontraktu

- [KSeF API produkcyjne](https://api.ksef.mf.gov.pl/docs/v2/index.html)
- [Oficjalny przewodnik integratora](https://github.com/CIRFMF/ksef-api)
- [Pobieranie faktur](https://github.com/CIRFMF/ksef-api/blob/main/pobieranie-faktur/pobieranie-faktur.md)
- [Synchronizacja przyrostowa](https://github.com/CIRFMF/ksef-api/blob/main/pobieranie-faktur/przyrostowe-pobieranie-faktur.md)
- [Limity API](https://github.com/CIRFMF/ksef-api/blob/main/limity/limity-api.md)
