# Historia zmian

## 0.7.1

- Status płatności FA(3), płatności częściowe i rozdzielenie wartości dokumentu od salda podanego przez wystawcę.
- Niepełne kwoty i daty KSeF są odrzucane zamiast zastępowania zerami.
- Każdy fizyczny GET XML, także ponowienie po HTTP 401, rezerwuje limit API.
- Podgląd A4 mierzy rzeczywisty tekst i dzieli bardzo długie opisy bez powielania kwot.
- Ostrzeżenie o niezapisanych ustawieniach i ochrona zmian MyDR przy zapisie KSeF.
- Trwałe checkpointy stron KSeF oraz wznowienie po limicie, błędzie i restarcie.
- Testy regresyjne protokołu i układu WPF wykonywane na Windows przed wydaniem.
