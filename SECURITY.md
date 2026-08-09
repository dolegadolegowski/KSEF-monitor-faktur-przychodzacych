# Bezpieczeństwo

## Zgłaszanie problemów

Problemy bezpieczeństwa zgłaszaj przez prywatne zgłoszenie podatności GitHub (`Security` → `Report a vulnerability`). Nie wklejaj tokenów KSeF, Client Secret, Refresh Tokenów, nagłówków `Authorization`, XML-i faktur ani danych pacjentów do publicznego zgłoszenia. Opisz objawy bez danych produkcyjnych i — jeżeli będzie potrzebny materiał diagnostyczny — najpierw usuń lub zastąp wszystkie dane identyfikujące.

## Przechowywanie danych

Aplikacja nie zawiera stałych poświadczeń. Dane wpisane w ustawieniach są zapisywane w `%LOCALAPPDATA%\KSeF Monitor` i chronione Windows DPAPI dla bieżącego konta użytkownika. Pliki `.dat` nie powinny być kopiowane do repozytorium, załączane do zgłoszeń ani udostępniane innym osobom.

Dziennik automatycznie maskuje najczęstsze formaty sekretów. Redakcja jest dodatkową ochroną, a nie gwarancją anonimizacji wszystkich danych wprowadzonych przez zewnętrzne systemy; przed udostępnieniem logu należy go przeczytać.

## Unieważnienie poświadczeń

Po podejrzeniu ujawnienia sekretu natychmiast unieważnij go w systemie źródłowym, wygeneruj nowy i zaktualizuj ustawienia aplikacji. Samo usunięcie lokalnego pliku lub historii Git nie unieważnia poświadczenia.

## Aktualizacje aplikacji

Aktualizator korzysta wyłącznie z publicznego, niezmiennego GitHub Release tego repozytorium. Weryfikuje HTTPS, właściciela i nazwę repozytorium, pole `immutable`, stabilny numer wersji, rozmiary assetów, digesty SHA-256 GitHub, plik `.sha256` i końcowy hash EXE. Workflow wymaga jednorazowego potwierdzenia konfiguracji przez zmienną repozytorium `IMMUTABLE_RELEASES_ENABLED=true`, publikuje wydanie dopiero po weryfikacji szkicu i po publikacji sprawdza faktyczne pole `immutable` oraz wygenerowane przez GitHub poświadczenie wydania. Podmiana pliku jest wykonywana atomowo z kopią zapasową oraz automatycznym rollbackiem, jeżeli nowa wersja nie potwierdzi poprawnego startu.

Niezmienność i sumy SHA-256 zabezpieczają integralność opublikowanego kanału GitHub, ale nie zastępują niezależnego podpisu wydawcy: osoba, która przejmie uprawnienia do publikowania nowych wydań, nadal mogłaby opublikować nowy złośliwy plik wraz z prawidłowymi hashami. Produkcyjne wydania powinny docelowo otrzymać podpis Authenticode z chronionego klucza code-signing, a aplikacja powinna weryfikować oczekiwanego wydawcę; prywatnego klucza ani certyfikatu PFX nie wolno dodawać do repozytorium.
