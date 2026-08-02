# Bezpieczeństwo

## Zgłaszanie problemów

Problemy bezpieczeństwa zgłaszaj przez prywatne zgłoszenie podatności GitHub (`Security` → `Report a vulnerability`). Nie wklejaj tokenów KSeF, Client Secret, Refresh Tokenów, nagłówków `Authorization`, XML-i faktur ani danych pacjentów do publicznego zgłoszenia. Opisz objawy bez danych produkcyjnych i — jeżeli będzie potrzebny materiał diagnostyczny — najpierw usuń lub zastąp wszystkie dane identyfikujące.

## Przechowywanie danych

Aplikacja nie zawiera stałych poświadczeń. Dane wpisane w ustawieniach są zapisywane w `%LOCALAPPDATA%\KSeF Monitor` i chronione Windows DPAPI dla bieżącego konta użytkownika. Pliki `.dat` nie powinny być kopiowane do repozytorium, załączane do zgłoszeń ani udostępniane innym osobom.

Dziennik automatycznie maskuje najczęstsze formaty sekretów. Redakcja jest dodatkową ochroną, a nie gwarancją anonimizacji wszystkich danych wprowadzonych przez zewnętrzne systemy; przed udostępnieniem logu należy go przeczytać.

## Unieważnienie poświadczeń

Po podejrzeniu ujawnienia sekretu natychmiast unieważnij go w systemie źródłowym, wygeneruj nowy i zaktualizuj ustawienia aplikacji. Samo usunięcie lokalnego pliku lub historii Git nie unieważnia poświadczenia.
