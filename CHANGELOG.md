# Historia zmian

## 0.8.0

- Stała kolekcja faktur zachowuje sortowanie, zaznaczenie i przewinięcie; przeczytany dokument nie przeskakuje między grupami.
- Lokalne wyszukiwanie po sprzedawcy, NIP-ie i numerach oraz filtry NOWE, korekt i waluty; suma widocznych wyników jest oddzielona od sumy miesiąca.
- Widoczne daty poprawnej synchronizacji KSeF i MyDR oraz komunikaty pustej listy.
- Postęp synchronizacji, anulowanie MyDR i kontynuacja pobierania po schowaniu ustawień.
- Dopasowanie A4 do szerokości, zoom, drukowanie, zapis PDF zgodnego z podglądem oraz eksport oryginalnego XML.
- Komunikaty XML opisują rzeczywiste ponowienie pobrania na żądanie.
- Zapamiętywanie położenia, wielkości i maksymalizacji okna oraz objaśnienie zamknięcia do traya.
- Układ dla mniejszych ekranów i skalowania Windows, przewijane ustawienia, etykiety dostępności i skróty Ctrl+F, F5, Alt+strzałki.
- Eksport PDF jest obrazem stron A4 o rozdzielczości 144 DPI, bez warstwy przeszukiwanego tekstu.

## 0.7.1

- Status płatności FA(3), płatności częściowe i rozdzielenie wartości dokumentu od salda podanego przez wystawcę.
- Niepełne kwoty i daty KSeF są odrzucane zamiast zastępowania zerami.
- Każdy fizyczny GET XML, także ponowienie po HTTP 401, rezerwuje limit API.
- Podgląd A4 mierzy rzeczywisty tekst i dzieli bardzo długie opisy bez powielania kwot.
- Ostrzeżenie o niezapisanych ustawieniach i ochrona zmian MyDR przy zapisie KSeF.
- Trwałe checkpointy stron KSeF oraz wznowienie po limicie, błędzie i restarcie.
- Testy regresyjne protokołu i układu WPF wykonywane na Windows przed wydaniem.
