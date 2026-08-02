# KSeF Monitor

Desktopowa aplikacja Windows 11 do monitorowania faktur otrzymanych w Krajowym Systemie e-Faktur (KSeF API 2.0) oraz miesięcznego obrotu usług prywatnych z MyDR.

Aktualny etap: działająca wersja `0.5.0`. Aplikacja łączy się wyłącznie z produkcyjnymi API KSeF (`https://api.ksef.mf.gov.pl/v2/`) i MyDR (`https://edm.mydr.pl/secure/ext_api/`) i nie udostępnia wyboru środowiska.

## Zaimplementowane

- logowanie tokenem KSeF w kontekście NIP;
- pobieranie aktualnego klucza publicznego i obsługa `publicKeyId`;
- RSA-OAEP/SHA-256 dla `token|timestampMs`;
- wymiana `AuthenticationToken` na access/refresh token oraz automatyczne odświeżanie;
- przyrostowe zapytania `POST /invoices/query/metadata` dla `Subject2`;
- synchronizacja po `PermanentStorage`, sortowanie rosnące, HWM, paginacja i deduplikacja po numerze KSeF;
- automatyczne odświeżanie co 15 minut oraz przycisk ręcznego odświeżania z sekundowym odliczaniem;
- lista bieżącego miesiąca i trzech poprzednich miesięcy;
- nagłówek `Faktury kosztowe` z miesięcznym podsumowaniem kwot brutto, oddzielnie dla każdej waluty;
- nagłówek `Obrót MyDR` dla wybranego miesiąca;
- osobne połączenie OAuth MyDR używające wyłącznie Client ID, Client Secret i Refresh Tokena;
- dzienna synchronizacja MyDR według dnia w strefie Europe/Warsaw oraz ręczne wymuszenie w ustawieniach;
- bezpieczna obsługa rotacji Refresh Tokena, codzienne pełne przeliczenie bieżącego miesiąca i cache starszych wizyt;
- trwały status `NOWA` i zielone podświetlenie całego wiersza, usuwane po pierwszym poprawnym wyświetleniu faktury;
- pobieranie XML nowych dokumentów z tempem zgodnym z limitem API;
- priorytetowe uzupełnianie brakującego XML przy otwieraniu faktury oraz automatyczne odświeżenie podglądu po pobraniu;
- wielostronicowy podgląd dokumentu w proporcjach A4 z danymi stron, tabelą pozycji, płatnością i podsumowaniem;
- okno szczegółów: metadane, pozycje z ilością, jednostką, cenami i wartościami netto/brutto, VAT oraz rabatem, wszystkie pola i surowy XML;
- odczyt pozycji z FA(3), alternatywnych pól brutto oraz dokumentów PEF/UBL;
- minimalizacja do traya z osadzoną, wielorozdzielczą ikoną `KSEF` i powiadomienia o nowych fakturach;
- szyfrowanie tokenów, danych MyDR i lokalnych cache za pomocą Windows DPAPI (`CurrentUser`), z oddzielnymi celami ochrony;
- izolacja cache i punktu HWM dla konkretnego NIP-u; zmiana kontekstu bezpiecznie zeruje dane poprzedniej firmy;
- atomowy zapis danych z automatyczną kopią `.bak` i próbą odzyskania po uszkodzeniu pliku;
- respektowanie `Retry-After` oraz przerwanie kolejki pobierania XML po odpowiedzi HTTP 429;
- parsowanie dużych XML-i poza wątkiem interfejsu i leniwe ładowanie ciężkich zakładek;
- proste komunikaty błędów na dolnej belce, wyróżnione na czerwono i automatycznie ukrywane po 30 sekundach;
- zakładka `Dziennik` w ustawieniach z centralną redakcją tokenów, nagłówków Authorization, JWT i kluczy prywatnych;
- tryb offline dla już zsynchronizowanych danych;
- pojedyncza instancja aplikacji; ponowne uruchomienie przywraca istniejące okno z traya.

## Wymagania deweloperskie

- Windows 11 x64;
- .NET SDK 10.0.302 lub nowszy zgodny z `global.json`;
- do testów integracyjnych KSeF: NIP i token z uprawnieniem `InvoiceRead`;
- opcjonalnie do integracji MyDR: Client ID, Client Secret i Refresh Token z zakresem `external_api`.

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

### Konfiguracja MyDR

W zakładce `MyDR` w ustawieniach:

1. Wprowadź Client ID, Client Secret i Refresh Token otrzymane dla zewnętrznego API MyDR.
2. Kliknij `Zapisz dane MyDR`. Sekrety zostaną zapisane razem w chronionym pakiecie Windows DPAPI.
3. Poczekaj na automatyczną synchronizację albo kliknij `Odśwież teraz`.

Zapisane sekrety nie są ponownie wyświetlane. Puste pola Client Secret i Refresh Token zachowują aktualnie zapisane wartości. Przycisk `Usuń dane MyDR` usuwa poświadczenia i wyłącza tę integrację.

## Testy

```powershell
dotnet run --project .\tests\KsefMonitor.SmokeTests\KsefMonitor.SmokeTests.csproj
```

Test obejmuje:

- walidację NIP;
- parser przykładowej faktury FA(3), wariantu wartości brutto oraz PEF/UBL;
- algorytm podziału długiej faktury na strony A4 bez utraty lub zmiany kolejności pozycji;
- miesięczne sumowanie kwot brutto i rozdzielanie różnych walut;
- protokół OAuth MyDR, dokładny zestaw pól formularza, produkcyjny host, Bearer token i rotację Refresh Tokena;
- paginację prywatnych wizyt MyDR bez używania hosta z pola `next`;
- klasyfikację wykonanych wizyt, ścisłe parsowanie wartości usług i dzienny harmonogram czasu polskiego;
- redakcję sekretów w zwykłym oraz rotowanym dzienniku;
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
- `mydr-credentials.dat` — Client ID, Client Secret i Refresh Token chronione DPAPI;
- `mydr-state.dat` — chronione miesięczne podsumowania oraz minimalny cache wizyt bez nazw pacjentów, personelu i usług;
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

## Zachowanie synchronizacji MyDR

- po uruchomieniu aplikacja sprawdza, czy w bieżącym dniu czasu polskiego wykonano już próbę; jeżeli tak, czeka do następnego dnia do godziny 00:05;
- ręczne `Odśwież teraz` w ustawieniach omija ograniczenie jednej próby dziennie;
- pobierane są cztery widoczne miesiące: bieżący i trzy poprzednie;
- źródłem są wizyty prywatne w stanach `Do rozliczenia`, `Oczekuje na płatność`, `Zakończona`, `Zamknięta` lub `Archiwalna`;
- miesiąc jest wyznaczany z pola `Visit.date`, a kwota jako suma pola `value` usług zwróconych przez `GET /visits/{id}/services/`;
- aplikacja nie zapisuje danych pacjentów, nazw personelu ani nazw usług; lokalnie pozostają tylko identyfikator wizyty, data, stan, znacznik modyfikacji, liczba usług i suma;
- automatyczna próba ponownie pobiera wszystkie usługi wykonanych wizyt z bieżącego miesiąca; dla trzech starszych miesięcy pobiera je ponownie, gdy zmieniło się `latest_modification`; ręczne `Odśwież teraz` celowo przelicza wszystkie wykonane wizyty w czterech miesiącach i może wykorzystać znacznie więcej wywołań API;
- niepełna odpowiedź lub niepoprawna kwota przerywa nową migawkę, dzięki czemu ostatni poprawny wynik nie zostaje zastąpiony zaniżoną sumą;
- po nieudanej automatycznej próbie kolejna odbędzie się następnego dnia; wcześniej można użyć ręcznego odświeżenia.

Publiczny Swagger MyDR nie udostępnia jednego raportu „wykonana procedura — kwota brutto”. Endpoint ICD-9 procedur nie zawiera ceny, a pole `value` usługi przypiętej do wizyty nie jest w schemacie opisane wprost jako brutto. Dokumentacja nie gwarantuje też wprost, że zmiana przypiętej usługi zawsze zmienia `Visit.latest_modification`; dlatego ręczne odświeżenie pomija cache i wykonuje pełne przeliczenie. Aplikacja traktuje `value` jako końcową wartość brutto usługi — zgodnie z celem tego monitora — ale przed użyciem wyniku do rozliczeń księgowych należy porównać go z raportem kontrolnym MyDR i potwierdzić z dostawcą znaczenie pola oraz listę stanów.

## Publiczne wydania i przyszłe aktualizacje

Repozytorium źródłowe i kanał przyszłych aktualizacji: [KSEF-monitor-faktur-przychodzacych](https://github.com/dolegadolegowski/KSEF-monitor-faktur-przychodzacych).

Adres repozytorium oraz endpoint najnowszego GitHub Release są zapisane w metadanych projektu i `ProductInformation`. Workflow dla tagów `v*` buduje `KSeFMonitor.exe`, oblicza SHA-256 i publikuje oba pliki jako GitHub Release. Runtime'owe poświadczenia są zapisywane wyłącznie poza repozytorium w `%LOCALAPPDATA%\KSeF Monitor`.

## Ważne przed wdrożeniem

1. Podpisać `KSeFMonitor.exe` certyfikatem code-signing, aby ograniczyć ostrzeżenia SmartScreen.
2. Zweryfikować token na kontrolowanym koncie produkcyjnym z minimalnym uprawnieniem `InvoiceRead`.
3. Dodać automatyczny proces sprawdzania zmian OpenAPI KSeF przed każdym wydaniem.
4. Przy organizacjach odbierających więcej niż 60 nowych faktur na godzinę rozszerzyć pobieranie XML o `/invoices/exports`; obecna wersja bezpiecznie kolejkuje nadmiar na następne cykle.
5. Client Secret w aplikacji desktopowej jest chroniony na dysku przez DPAPI, ale podczas działania musi znaleźć się w pamięci procesu. Jeżeli polityka MyDR wymaga klienta `confidential` odpornego na przejęcie komputera użytkownika, należy przenieść OAuth do kontrolowanego backendu/proxy.

## Źródła kontraktu

- [KSeF API produkcyjne](https://api.ksef.mf.gov.pl/docs/v2/index.html)
- [Oficjalny przewodnik integratora](https://github.com/CIRFMF/ksef-api)
- [Pobieranie faktur](https://github.com/CIRFMF/ksef-api/blob/main/pobieranie-faktur/pobieranie-faktur.md)
- [Synchronizacja przyrostowa](https://github.com/CIRFMF/ksef-api/blob/main/pobieranie-faktur/przyrostowe-pobieranie-faktur.md)
- [Limity API](https://github.com/CIRFMF/ksef-api/blob/main/limity/limity-api.md)
- [MyDR — dokumentacja zewnętrznego API](https://api.edm.mydr.pl/api-docs/)
- [MyDR — uzyskanie dostępu do API](https://api.edm.mydr.pl/api-contact-request/)
