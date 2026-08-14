# KSeF Monitor

Desktopowa aplikacja Windows 11 do monitorowania faktur otrzymanych w Krajowym Systemie e-Faktur (KSeF API 2.0) oraz miesięcznego obrotu usług prywatnych z MyDR.

Aktualny etap: działająca wersja `0.7.0`. Aplikacja łączy się wyłącznie z produkcyjnymi API KSeF (`https://api.ksef.mf.gov.pl/v2/`) i MyDR (`https://edm.mydr.pl/secure/ext_api/`) i nie udostępnia wyboru środowiska.

## Zaimplementowane

- logowanie tokenem KSeF w kontekście NIP;
- pobieranie aktualnego klucza publicznego i obsługa `publicKeyId`;
- RSA-OAEP/SHA-256 dla `token|timestampMs`;
- wymiana `AuthenticationToken` na access/refresh token oraz automatyczne odświeżanie;
- przyrostowe zapytania `POST /invoices/query/metadata` dla `Subject2`;
- synchronizacja po `PermanentStorage`, sortowanie rosnące, HWM, paginacja i deduplikacja po numerze KSeF;
- automatyczne odświeżanie co 15 minut oraz przycisk ręcznego odświeżania z sekundowym odliczaniem;
- lista bieżącego miesiąca i trzech poprzednich miesięcy;
- liczbowe sortowanie kwot brutto, również dla korekt ujemnych i wartości z separatorami tysięcy;
- nagłówek `Faktury kosztowe` z miesięcznym podsumowaniem kwot brutto, oddzielnie dla każdej waluty;
- nagłówek `Obrót MyDR` dla wybranego miesiąca;
- popup po najechaniu na kwotę `Obrót MyDR`, pokazujący miesięczny obrót według lekarza/personelu; osoby z saldem równym dokładnie `0,00 PLN` są pomijane, a korekty ujemne pozostają widoczne;
- osobne połączenie OAuth MyDR używające wyłącznie Client ID, Client Secret i Refresh Tokena;
- dzienna synchronizacja MyDR według dnia w strefie Europe/Warsaw oraz ręczne wymuszenie w ustawieniach;
- bezpieczna obsługa rotacji Refresh Tokena oraz przyrostowe przeliczanie nowych lub zmienionych wizyt we wszystkich czterech miesiącach;
- trwały status `NOWA` i zielone podświetlenie całego wiersza, usuwane po pierwszym poprawnym wyświetleniu faktury;
- pobieranie pełnego XML dopiero po otwarciu faktury, z tempem zgodnym z limitem API i automatycznym odświeżeniem podglądu po pobraniu;
- wielostronicowy podgląd dokumentu w proporcjach A4 z danymi stron, tabelą pozycji, płatnością i podsumowaniem;
- okno szczegółów: metadane, pozycje z ilością, jednostką, cenami i wartościami netto/brutto, VAT oraz rabatem, wszystkie pola i surowy XML;
- odczyt pozycji z FA(3), alternatywnych pól brutto oraz dokumentów PEF/UBL;
- uzupełnianie brakującego VAT i brutto pozycji z wartości netto oraz stawki, bez nadpisywania kwot zapisanych przez wystawcę; wartości pochodne są oznaczone `*` w podglądzie;
- minimalizacja do traya z osadzoną, wielorozdzielczą ikoną `KSEF` i powiadomienia o nowych fakturach;
- szyfrowanie tokenów, danych MyDR i lokalnych cache za pomocą Windows DPAPI (`CurrentUser`), z oddzielnymi celami ochrony;
- izolacja cache i punktu HWM dla konkretnego NIP-u; zmiana kontekstu bezpiecznie zeruje dane poprzedniej firmy;
- atomowy zapis danych z automatyczną kopią `.bak` i próbą odzyskania po uszkodzeniu pliku;
- respektowanie `Retry-After` i trwały cooldown API po odpowiedzi HTTP 429;
- wykładniczy backoff dla XML pojedynczej niedostępnej faktury (15 min → 1 h → 4 h → maks. 24 h);
- parsowanie dużych XML-i poza wątkiem interfejsu i leniwe ładowanie ciężkich zakładek;
- proste komunikaty błędów na dolnej belce, wyróżnione na czerwono i automatycznie ukrywane po 30 sekundach;
- zakładka `Dziennik` w ustawieniach z centralną redakcją tokenów, nagłówków Authorization, JWT i kluczy prywatnych;
- tryb offline dla już zsynchronizowanych danych;
- pojedyncza instancja aplikacji; ponowne uruchomienie przywraca istniejące okno z traya;
- automatyczne sprawdzanie publicznego GitHub Release przy uruchomieniu i po otwarciu ustawień;
- aktualizacja jednym kliknięciem z kontrolą wersji, rozmiaru, digestu GitHub i pliku SHA-256;
- bezpieczna podmiana pojedynczego EXE po zamknięciu programu, automatyczny restart, health-check nowej wersji i rollback do kopii zapasowej po nieudanym starcie.

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
- dokładne liczniki żądań MyDR, pojedyncze ponowienie po 401, brak ponowienia po 429, zachowanie `Retry-After` i minimalny odstęp między wywołaniami;
- paginację prywatnych wizyt MyDR bez używania hosta z pola `next`;
- deserializację osoby realizującej wizytę, agregację po jej stabilnym ID, rozdzielenie identycznych nazwisk, obsługę zmian nazwy, sald zerowych, korekt ujemnych i brakujących nazw;
- migrację starszego stanu MyDR oraz głębokie kopiowanie miesięcznej listy obrotów personelu;
- klasyfikację wykonanych wizyt, bezpieczne parsowanie tekstowych i liczbowych wartości usług oraz dzienny harmonogram czasu polskiego;
- redakcję sekretów w zwykłym oraz rotowanym dzienniku;
- generowanie i odszyfrowanie żądania RSA-OAEP;
- cały przebieg challenge → auth → status → redeem;
- dokładną liczbę żądań uwierzytelnienia KSeF, ponowienie po 401 oraz brak kosztownego pełnego logowania po błędzie refresh 429/503;
- sprawdzenie filtra `Subject2`, `PermanentStorage`, obowiązkowego HWM oraz minimalnego odstępu między zapytaniami o metadane;
- mapowanie metadanych faktury;
- pobieranie pełnego XML po numerze KSeF i zachowanie statusu `NOWA` w migawkach listy;
- wymuszenie produkcyjnego adresu API;
- ścisły parser SemVer i blokadę downgrade/prerelease;
- walidację metadanych GitHub Release, dokładnych adresów i hostów przekierowań;
- scalanie równoległych sprawdzeń aktualizacji oraz twardy odstęp między ręcznymi zapytaniami do GitHub API;
- parser pliku `.sha256`, strumieniowe pobieranie z kontrolą rozmiaru i hasha;
- atomową podmianę pliku z kopią zapasową oraz transakcję rollbacku.

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
- `metadata-rate.dat` — mały, chroniony DPAPI licznik godzinowego limitu zapytań o listę faktur;
- `mydr-credentials.dat` — Client ID, Client Secret i Refresh Token chronione DPAPI;
- `mydr-state.dat` — chronione miesięczne podsumowania, zagregowane nazwy personelu z obrotem oraz minimalny cache wizyt; bez nazw pacjentów i usług;
- `app.log` oraz `app.previous.log` — rotowany dziennik diagnostyczny aplikacji (maksymalnie około 2 MB łącznie).

Przy kolejnych zapisach aplikacja zachowuje poprzednią poprawną wersję każdego pliku jako `.bak`. Kopia jest używana automatycznie, gdy podstawowy plik jest uszkodzony lub niekompletny.

Plików `.dat` nie da się odszyfrować na innym koncie Windows. Usunięcie folderu resetuje aplikację i lokalny cache.

## Zachowanie synchronizacji

- pierwsza synchronizacja zaczyna się od początku trzeciego poprzedniego miesiąca i automatycznie dzieli zakres na przylegające, dwumiesięczne okna zgodne z limitem API;
- aktualizacja z wersji o krótszym zakresie automatycznie cofa HWM i jednorazowo uzupełnia brakującą historię;
- dokumenty pobrane wyłącznie przez takie uzupełnienie historii nie wywołują zaległych oznaczeń `NOWA` ani powiadomień;
- kolejne używają dokładnej wartości ostatniego HWM; błąd `21183` uruchamia bezpieczną odbudowę widocznego zakresu z deduplikacją po numerze KSeF;
- niepełna odpowiedź bez poprawnego HWM nie jest zatwierdzana, dzięki czemu aplikacja nie traci punktu synchronizacji i nie przechodzi po cichu do pełnego skanowania przy każdym cyklu;
- kolejne strony metadanych są oddalone o co najmniej 4 sekundy; wspólny, trwały licznik synchronizacji i testu połączenia dopuszcza najwyżej 18 żądań w dowolnej godzinie, pozostawiając margines względem limitu KSeF 20/h;
- automatyczna synchronizacja pobiera wyłącznie lekkie metadane; pełny XML jest pobierany dopiero po otwarciu konkretnej faktury i potem pozostaje w lokalnym cache;
- aplikacja prowadzi trwały licznik pobrań XML i wykonuje najwyżej 60 w dowolnym oknie godzinnym;
- kolejne pobrania XML są oddalone o co najmniej 4 sekundy (maks. 15/min), co zostawia margines względem produkcyjnych limitów KSeF 64/h i 16/min;
- błąd dotyczący pojedynczego XML nie jest ponawiany przy każdym kliknięciu: odstęp rośnie do maksymalnie 24 godzin;
- chwilowy błąd 429/5xx podczas odnowienia tokena nie uruchamia pełnego challenge i nie generuje dodatkowej serii żądań;
- ręczne odświeżenie można wymusić przyciskiem w dowolnym momencie, o ile inna synchronizacja właśnie nie trwa;
- po poprawnej synchronizacji licznik rozpoczyna kolejne 15 minut;
- przy błędzie sieci aplikacja zachowuje cache i ponawia próbę po 15 minutach;
- odpowiedzi Problem Details pokazują właściwy kod i szczegóły KSeF zamiast samego ogólnego opisu;
- HTTP 429 nie jest maskowany — komunikat zawiera `Retry-After` zwrócone przez KSeF.

Przy wolumenie wymagającym ponad 18 stron metadanych w jednej godzinie aplikacja zatrzyma paginację przed limitem serwera i wyświetli błąd zamiast przeciążać API. Dla podmiotów o bardzo dużym wolumenie docelowym mechanizmem KSeF jest asynchroniczny eksport faktur; bieżąca wersja monitora jest zoptymalizowana pod zwykły wolumen kliniki.

## Zachowanie synchronizacji MyDR

- po uruchomieniu aplikacja sprawdza, czy w bieżącym dniu czasu polskiego wykonano już próbę; jeżeli tak, czeka do następnego dnia do godziny 00:05;
- ręczne `Odśwież teraz` w ustawieniach omija ograniczenie jednej próby dziennie;
- pobierane są cztery widoczne miesiące: bieżący i trzy poprzednie; lista wizyt używa jednego wspólnego zakresu dat zamiast czterech osobnych zapytań;
- źródłem są wizyty prywatne w stanach `Do rozliczenia`, `Oczekuje na płatność`, `Zakończona`, `Zamknięta` lub `Archiwalna`;
- miesiąc jest wyznaczany z pola `Visit.date`, a kwota jako suma pola `value` usług zwróconych przez `GET /visits/{id}/services/`;
- obrót według osoby jest grupowany po stabilnym polu `Visit.doctor`; wyświetlana nazwa pochodzi z `doctor_name` i `doctor_surname`, a brak nazwy nie powoduje utraty kwoty;
- pole `value` jest przyjmowane zarówno jako liczba JSON, jak i tekst dziesiętny, ponieważ rzeczywista odpowiedź MyDR może różnić się typem od publicznego schematu;
- aplikacja nie zapisuje danych pacjentów ani nazw usług; nazwa personelu jest zapisywana tylko raz w zagregowanym podsumowaniu miesiąca, a cache wizyt nadal zawiera wyłącznie identyfikator wizyty, datę, stan, znacznik modyfikacji, liczbę usług i sumę;
- pierwszy start po aktualizacji ze starszej wersji zachowuje dotychczasowe sumy i jednorazowo planuje świeże pobranie, ponieważ wcześniejszy cache nie zawierał danych personelu;
- automatyczna synchronizacja pobiera szczegóły usług tylko dla nowej wizyty, zmienionego `latest_modification`, zmienionej daty/stanu albo braku bezpiecznego znacznika modyfikacji;
- ręczne `Odśwież teraz` celowo omija cache i przelicza wszystkie wykonane wizyty w czterech widocznych miesiącach; jest to kontrolna, droższa operacja używana wtedy, gdy zmiana usługi mogła nie zmienić znacznika wizyty;
- kolejne żądania danych MyDR są rozłożone w czasie (maksymalnie około 2/s), a postęp cache jest zapisywany co 25 nowych odczytów usług, aby błąd sieci lub 429 nie wymuszał powtarzania całej ukończonej części;
- po HTTP 429 aplikacja zapisuje `Retry-After` i blokuje również ręczne ponowienie do końca wskazanej przerwy;
- niepełna odpowiedź lub niepoprawna kwota przerywa nową migawkę, dzięki czemu ostatni poprawny wynik nie zostaje zastąpiony zaniżoną sumą;
- po nieudanej automatycznej próbie kolejna odbędzie się następnego dnia; wcześniej można użyć ręcznego odświeżenia.

Publiczny Swagger MyDR nie udostępnia jednego raportu „wykonana procedura — kwota brutto”. Endpoint ICD-9 procedur nie zawiera ceny, a pole `value` usługi przypiętej do wizyty nie jest w schemacie opisane wprost jako brutto. Cache wykorzystuje opisane przez MyDR pole `Visit.latest_modification` („data ostatniej modyfikacji danych wizyty”); wizyta bez tego pola jest zawsze pobierana ponownie. Dokumentacja nie gwarantuje wprost, że zmiana przypiętej usługi zawsze zmienia ten znacznik, dlatego ręczne odświeżenie wykonuje pełne przeliczenie. Aplikacja traktuje `value` jako końcową wartość brutto usługi — zgodnie z celem tego monitora — ale przed użyciem wyniku do rozliczeń księgowych należy porównać go z raportem kontrolnym MyDR i potwierdzić z dostawcą znaczenie pola oraz listę stanów.

Publiczny cennik API MyDR wskazuje limit bezpłatnego użycia poniżej 5000 zapytań miesięcznie. Zwykła synchronizacja dzienna korzysta więc z cache i pobiera ponownie tylko nowe lub zmienione wizyty. Ręczne pełne przeliczenie jest oznaczone w interfejsie jako kosztowniejsze i powinno służyć do kontroli, a nie do częstego odświeżania.

## Automatyczne aktualizacje

Repozytorium źródłowe i jedyny kanał aktualizacji: [KSEF-monitor-faktur-przychodzacych](https://github.com/dolegadolegowski/KSEF-monitor-faktur-przychodzacych).

Wersja `0.6.0` jest pierwszą zawierającą aktualizator, dlatego przejście z `0.5.2` lub starszej wymaga jeszcze jednego ręcznego pobrania `KSeFMonitor.exe`. Każda kolejna stabilna wersja jest wykrywana automatycznie przy starcie aplikacji i po otwarciu ustawień. Gdy GitHub udostępni nowsze wydanie, obok numeru wersji pojawia się przycisk `Aktualizuj`; ten sam mechanizm jest dostępny w zakładce `Aktualizacje`.

Start aplikacji i otwarcie ustawień współdzielą pięciominutowy cache wyniku. Przycisk ręcznego sprawdzenia omija ten cache, ale zachowuje co najmniej minutę odstępu między zapytaniami, aby wielokrotne klikanie nie wyczerpywało publicznego limitu GitHub API.

Instalacja nie odbywa się bez wiedzy użytkownika: aplikacja automatycznie wykrywa nową wersję, a pobranie, zamknięcie programu i podmiana EXE rozpoczynają się dopiero po kliknięciu `Aktualizuj` i potwierdzeniu komunikatu.

Aktualizator:

- odrzuca szkice, prerelease, modyfikowalne wydania, niepoprawne tagi i wersje starsze lub równe zainstalowanej;
- przyjmuje tylko HTTPS i dokładne adresy tego repozytorium oraz dozwolony host plików GitHub Releases;
- wymaga dokładnie jednego `KSeFMonitor.exe` i jednego `KSeFMonitor.exe.sha256`;
- porównuje rozmiar, digest SHA-256 z metadanych GitHub, zawartość pliku `.sha256` i hash pobranego EXE;
- pobiera plik strumieniowo do unikalnego katalogu na tym samym dysku co aplikacja;
- po potwierdzeniu użytkownika uruchamia kopię bieżącego, wcześniej zweryfikowanego EXE w trybie instalatora, atomowo podmienia plik przez `File.Replace` i zachowuje backup;
- czeka na sygnał poprawnego pokazania głównego okna nowej wersji; brak sygnału lub awaria startu uruchamia rollback i ponowny start starej wersji;
- nie usuwa kopii potrzebnej do odzyskania przerwanej transakcji i rozpoznaje stan aktualizacji przy kolejnym uruchomieniu;
- nigdy nie zapisuje tokena GitHub ani nie podnosi automatycznie uprawnień administratora.

Jeżeli aplikacja znajduje się w katalogu bez prawa zapisu, automatyczna instalacja wyświetli prosty komunikat i pozostawi dotychczasowy EXE bez zmian. Wtedy należy pobrać plik ręcznie ze strony wydania. Dane KSeF i MyDR pozostają w `%LOCALAPPDATA%\KSeF Monitor` i nie są przenoszone ani usuwane przez aktualizator.

Repozytorium musi mieć włączone **Immutable Releases** oraz zmienną Actions `IMMUTABLE_RELEASES_ENABLED=true`, ustawioną dopiero po włączeniu tej ochrony. Workflow dla tagów `vMAJOR.MINOR.PATCH` nie utworzy szkicu bez tego jednorazowego potwierdzenia. Następnie sprawdza zgodność wersji projektu, uruchamia testy jednostkowe oraz integracyjny test rzeczywistej podmiany i rollbacku, po czym buduje pojedynczy EXE. GitHub Release powstaje najpierw jako szkic, jego dwa assety są porównywane z lokalnym buildem, a dopiero zweryfikowany szkic staje się publicznym `latest`. Na końcu workflow niezależnie sprawdza faktyczną niezmienność wydania, poświadczenie GitHub Release, zgodność obu lokalnych assetów i publiczny endpoint używany przez aplikację. Aplikacja odrzuca wydanie, którego API GitHuba nie oznaczy jako niezmienne. Ponowne uruchomienie workflow bezpiecznie wznawia istniejący szkic, ale nigdy nie nadpisuje opublikowanego wydania.

Jednorazowa konfiguracja repozytorium wykonywana przez właściciela po `gh auth login -h github.com`:

```powershell
gh api --method PUT -H "Accept: application/vnd.github+json" -H "X-GitHub-Api-Version: 2026-03-10" repos/dolegadolegowski/KSEF-monitor-faktur-przychodzacych/immutable-releases
gh variable set IMMUTABLE_RELEASES_ENABLED --repo dolegadolegowski/KSEF-monitor-faktur-przychodzacych --body true
```

Nową wersję publikuje się przez zmianę `Version`, `AssemblyVersion` i `FileVersion`, commit oraz wypchnięcie odpowiadającego tagu, na przykład `v0.7.1`. Dopiero zakończony powodzeniem workflow udostępnia EXE aplikacji.

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
- [MyDR — dokumentacja zewnętrznego API](https://edm.mydr.pl/api-docs/)
- [MyDR — specyfikacja OpenAPI](https://edm.mydr.pl/api-docs/?format=openapi)
