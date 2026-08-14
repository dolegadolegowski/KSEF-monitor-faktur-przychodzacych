using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KsefMonitor;

const string sampleInvoice = """
<?xml version="1.0" encoding="utf-8"?>
<Faktura xmlns="http://crd.gov.pl/wzor/2025/06/25/13775/">
  <Podmiot1><DaneIdentyfikacyjne><NIP>5265877635</NIP><Nazwa>Sprzedawca Testowy</Nazwa></DaneIdentyfikacyjne></Podmiot1>
  <Podmiot2><DaneIdentyfikacyjne><NIP>1234563218</NIP><Nazwa>Nabywca Testowy</Nazwa></DaneIdentyfikacyjne></Podmiot2>
  <Fa>
    <KodWaluty>PLN</KodWaluty><P_1>2026-08-01</P_1><P_2>FV/1/08/2026</P_2>
    <FaWiersz><NrWierszaFa>1</NrWierszaFa><P_7>Usługa testowa</P_7><P_8A>szt.</P_8A><P_8B>2</P_8B><P_9A>100.00</P_9A><P_11>200.00</P_11><P_12>23</P_12></FaWiersz>
  </Fa>
</Faktura>
""";

var parsed = InvoiceXmlReader.Parse(sampleInvoice);
Require(parsed.Summary["Numer faktury"] == "FV/1/08/2026", "Nie odczytano numeru faktury.");
Require(parsed.Summary["Sprzedawca"] == "Sprzedawca Testowy", "Nie odczytano sprzedawcy.");
Require(parsed.Lines.Count == 1, "Nie odczytano pozycji faktury.");
Require(parsed.Lines[0].Description == "Usługa testowa", "Nie odczytano opisu pozycji.");
Require(parsed.Lines[0].Quantity == "2", "Nie odczytano ilości pozycji FA(3).");
Require(parsed.Lines[0].Unit == "szt.", "Nie odczytano jednostki miary pozycji FA(3).");
Require(parsed.Lines[0].UnitNetPrice == "100.00", "Nie odczytano ceny netto pozycji FA(3).");
Require(parsed.Lines[0].NetAmount == "200.00", "Nie odczytano wartości netto pozycji FA(3).");
Require(parsed.Lines[0].VatAmount == "46.00", "Nie wyliczono opcjonalnej kwoty VAT pozycji FA(3).");
Require(parsed.Lines[0].GrossAmount == "246.00", "Nie wyliczono wartości brutto pozycji FA(3).");
Require(parsed.Lines[0].IsVatAmountCalculated && parsed.Lines[0].IsGrossAmountCalculated,
    "Wyliczone kwoty pozycji nie zostały oznaczone jako pochodne.");
Require(parsed.Fields.Count > 10, "Nie spłaszczono wszystkich pól XML.");
TestGrossLineVariant();
TestCalculatedLineAmounts();
TestPefUblLineVariant();
TestA4Pagination();
TestMonthlyInvoiceSummary();
TestInvoiceAmountSortKey();
TestInvoiceNewState();
TestStateContextIsolation();
TestStatusBanner();
TestUserFacingErrors();
TestApplicationLog();
TestMyDrStateAndSchedule();
TestMyDrDoctorTurnovers();
TestApiTrafficPolicies();
TestAtomicMyDrStateReplacement();
Require(NipValidator.IsValid("526-587-76-35"), "Walidacja poprawnego NIP-u nie działa.");
Require(!NipValidator.IsValid("5265877634"), "Walidacja błędnego NIP-u nie działa.");
Require(!NipValidator.IsValid("0000000000"), "Walidacja zaakceptowała techniczny, niepoprawny NIP.");
Require(!NipValidator.IsValid("ABC5265877635"), "Walidacja zaakceptowała niedozwolone znaki w NIP-ie.");
Require(AppSettings.GetBaseUri() == new Uri("https://api.ksef.mf.gov.pl/v2/"), "Aplikacja nie używa produkcyjnego endpointu KSeF.");
await TestKsefProtocolAsync();
await TestMyDrProtocolAsync();
await TestMyDrRetryAndPacingAsync();
await UpdaterSmokeTests.RunAsync(Require);
if (args.Contains("--live-github", StringComparer.Ordinal))
{
    var expectedTagIndex = Array.IndexOf(args, "--expected-tag");
    Require(expectedTagIndex >= 0 && expectedTagIndex + 1 < args.Length,
        "Opcja --live-github wymaga --expected-tag vMAJOR.MINOR.PATCH.");
    await UpdaterSmokeTests.RunLiveGitHubCheckAsync(args[expectedTagIndex + 1], Require);
}
var publishedExeIndex = Array.IndexOf(args, "--published-exe");
var expectedVersionIndex = Array.IndexOf(args, "--expected-version");
if (publishedExeIndex >= 0 || expectedVersionIndex >= 0)
{
    Require(publishedExeIndex >= 0 && publishedExeIndex + 1 < args.Length &&
            expectedVersionIndex >= 0 && expectedVersionIndex + 1 < args.Length,
        "Opcje kontroli EXE wymagają --published-exe PATH i --expected-version VERSION.");
    UpdaterSmokeTests.CheckPublishedExecutable(args[publishedExeIndex + 1], args[expectedVersionIndex + 1], Require);
}

Console.WriteLine("KSeFMonitor smoke tests: OK");
return 0;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void TestGrossLineVariant()
{
    const string xml = """
    <Faktura xmlns="http://crd.gov.pl/wzor/2025/06/25/13775/">
      <Fa><FaWiersz>
        <NrWierszaFa>1</NrWierszaFa><P_7>Towar brutto</P_7><P_8A>szt.</P_8A><P_8B>3</P_8B>
        <P_9B>123.00</P_9B><P_10>3.00</P_10><P_11A>366.00</P_11A><P_11Vat>68.44</P_11Vat><P_12>23</P_12>
      </FaWiersz></Fa>
    </Faktura>
    """;

    var line = InvoiceXmlReader.Parse(xml).Lines[0];
    Require(line.Quantity == "3", "Nie odczytano ilości w wariancie brutto FA(3).");
    Require(line.UnitGrossPrice == "123.00", "Nie odczytano ceny brutto FA(3).");
    Require(line.Discount == "3.00", "Nie odczytano rabatu FA(3).");
    Require(line.GrossAmount == "366.00", "Nie odczytano wartości brutto FA(3).");
    Require(line.VatAmount == "68.44", "Nie odczytano kwoty VAT FA(3).");
    Require(!line.IsGrossAmountCalculated && !line.IsVatAmountCalculated,
        "Jawne kwoty FA(3) zostały błędnie oznaczone jako wyliczone.");
}

static void TestCalculatedLineAmounts()
{
    var netLine = ParseFaLine("<P_11>54.79</P_11><P_12>23</P_12>");
    Require(netLine.VatAmount == "12.60" && netLine.GrossAmount == "67.39",
        "Nie wyliczono VAT i brutto z P_11 oraz P_12 zgodnie z zaokrągleniem do groszy.");

    var grossLine = ParseFaLine("<P_11A>123.00</P_11A><P_12>23</P_12>");
    Require(grossLine.VatAmount == "23.00" && grossLine.GrossAmount == "123.00",
        "Nie wyliczono VAT dla wariantu ceny brutto P_11A.");

    var explicitVat = ParseFaLine("<P_11>100.00</P_11><P_11Vat>22.99</P_11Vat><P_12>23</P_12>");
    Require(explicitVat.VatAmount == "22.99" && explicitVat.GrossAmount == "122.99",
        "Jawna kwota P_11Vat nie otrzymała pierwszeństwa przed wyliczeniem.");
    Require(!explicitVat.IsVatAmountCalculated && explicitVat.IsGrossAmountCalculated,
        "Niepoprawnie oznaczono pochodzenie jawnego VAT lub wyliczonego brutto.");

    var correction = ParseFaLine("<P_11>-54.79</P_11><P_12>23</P_12>");
    Require(correction.VatAmount == "-12.60" && correction.GrossAmount == "-67.39",
        "Nie wyliczono poprawnie ujemnej pozycji korekty.");

    foreach (var (net, rate, expectedVat, expectedGross) in new[]
             {
                 ("40.65", "23", "9.35", "50.00"),
                 ("0.50", "23", "0.12", "0.62"),
                 ("-0.50", "23", "-0.12", "-0.62"),
                 ("0.10", "5", "0.01", "0.11"),
                 ("100.00", "8", "8.00", "108.00"),
                 ("-162.60", "23", "-37.40", "-200.00")
             })
    {
        var line = ParseFaLine($"<P_11>{net}</P_11><P_12>{rate}</P_12>");
        Require(line.VatAmount == expectedVat && line.GrossAmount == expectedGross,
            $"Niepoprawne zaokrąglenie dla {net} przy stawce {rate}%.");
    }

    foreach (var zeroRate in new[] { "0 KR", "0 WDT", "0 EX" })
    {
        var line = ParseFaLine($"<P_11>10.00</P_11><P_12>{zeroRate}</P_12>");
        Require(line.VatAmount == "0.00" && line.GrossAmount == "10.00",
            $"Nie obsłużono zerowej stawki VAT: {zeroRate}.");
    }

    foreach (var nonTaxable in new[] { "zw", "oo", "np I", "np II" })
    {
        var line = ParseFaLine($"<P_11>10.00</P_11><P_12>{nonTaxable}</P_12>");
        Require(string.IsNullOrEmpty(line.VatAmount) && line.GrossAmount == "10.00",
            $"Niepoprawnie przedstawiono pozycję nieopodatkowaną: {nonTaxable}.");
        Require(!line.IsVatAmountCalculated && line.IsGrossAmountCalculated,
            $"Niepoprawnie oznaczono pochodzenie kwot pozycji: {nonTaxable}.");
    }

    var ossLine = ParseFaLine("<P_11>100.00</P_11><P_12_XII>19</P_12_XII>");
    Require(ossLine.VatAmount == "19.00" && ossLine.GrossAmount == "119.00",
        "Nie wyliczono wartości dla stawki P_12_XII.");

    var incomplete = ParseFaLine("<P_11>10.00</P_11>");
    Require(string.IsNullOrEmpty(incomplete.VatAmount) && string.IsNullOrEmpty(incomplete.GrossAmount),
        "Brakująca stawka została bezpodstawnie zastąpiona wyliczoną kwotą.");

    var invalid = ParseFaLine("<P_11>wartość</P_11><P_12>nieznana</P_12>");
    Require(string.IsNullOrEmpty(invalid.VatAmount) && string.IsNullOrEmpty(invalid.GrossAmount),
        "Niepoprawne dane pozycji zostały bezpodstawnie użyte do wyliczenia.");

    var grossWithoutRate = ParseFaLine("<P_11A>2000.00</P_11A>");
    Require(grossWithoutRate.GrossAmount == "2000.00" && string.IsNullOrEmpty(grossWithoutRate.VatAmount),
        "Jawna wartość P_11A bez stawki nie została zachowana.");
}

static InvoiceLine ParseFaLine(string fields)
{
    var xml = $"<Faktura><Fa><FaWiersz><NrWierszaFa>1</NrWierszaFa>{fields}</FaWiersz></Fa></Faktura>";
    return InvoiceXmlReader.Parse(xml).Lines.Single();
}

static void TestPefUblLineVariant()
{
    const string xml = """
    <Invoice xmlns="urn:oasis:names:specification:ubl:schema:xsd:Invoice-2"
             xmlns:cac="urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2"
             xmlns:cbc="urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2">
      <cac:InvoiceLine>
        <cbc:ID>7</cbc:ID>
        <cbc:InvoicedQuantity unitCode="H87">4</cbc:InvoicedQuantity>
        <cbc:LineExtensionAmount currencyID="PLN">200.00</cbc:LineExtensionAmount>
        <cac:TaxTotal><cbc:TaxAmount currencyID="PLN">46.00</cbc:TaxAmount></cac:TaxTotal>
        <cac:Item><cbc:Name>Pozycja PEF</cbc:Name><cac:ClassifiedTaxCategory><cbc:Percent>23</cbc:Percent></cac:ClassifiedTaxCategory></cac:Item>
        <cac:Price><cbc:PriceAmount currencyID="PLN">50.00</cbc:PriceAmount></cac:Price>
      </cac:InvoiceLine>
    </Invoice>
    """;

    var line = InvoiceXmlReader.Parse(xml).Lines[0];
    Require(line.Number == "7", "Nie odczytano numeru pozycji PEF/UBL.");
    Require(line.Description == "Pozycja PEF", "Nie odczytano opisu pozycji PEF/UBL.");
    Require(line.Quantity == "4", "Nie odczytano ilości pozycji PEF/UBL.");
    Require(line.Unit == "H87", "Nie odczytano jednostki pozycji PEF/UBL.");
    Require(line.UnitNetPrice == "50.00", "Nie odczytano ceny pozycji PEF/UBL.");
    Require(line.NetAmount == "200.00", "Nie odczytano wartości pozycji PEF/UBL.");
    Require(line.VatAmount == "46.00", "Nie odczytano kwoty VAT pozycji PEF/UBL.");
    Require(line.GrossAmount == "246.00", "Nie wyliczono wartości brutto pozycji PEF/UBL.");
    Require(line.VatRate == "23", "Nie odczytano stawki VAT pozycji PEF/UBL.");
}

static void TestA4Pagination()
{
    var shortInvoice = Enumerable.Range(1, 8)
        .Select(index => new InvoiceLine(index.ToString(CultureInfo.InvariantCulture), $"Pozycja {index}", "1", "szt.", "10.00", string.Empty,
            string.Empty, "10.00", string.Empty, "2.30", "23"))
        .ToList();
    Require(InvoicePagePlanner.Plan(shortInvoice).Count == 1, "Typowa krótka faktura została niepotrzebnie podzielona na strony.");

    var lines = Enumerable.Range(1, 48)
        .Select(index => new InvoiceLine(
            index.ToString(CultureInfo.InvariantCulture),
            index % 7 == 0 ? new string('A', 240) : $"Pozycja {index}",
            "1",
            "szt.",
            "10.00",
            string.Empty,
            string.Empty,
            "10.00",
            string.Empty,
            "2.30",
            "23"))
        .ToList();

    var pages = InvoicePagePlanner.Plan(lines);
    Require(pages.Count > 1, "Długi dokument nie został podzielony na strony A4.");
    Require(pages[0].IsFirst && pages[^1].IsLast, "Niepoprawne oznaczenie pierwszej lub ostatniej strony A4.");
    Require(pages.Sum(page => page.Lines.Count) == lines.Count, "Paginacja A4 zgubiła pozycje faktury.");
    Require(pages.SelectMany(page => page.Lines).Select(line => line.Number).SequenceEqual(lines.Select(line => line.Number)),
        "Paginacja A4 zmieniła kolejność pozycji faktury.");

    var oversized = new InvoiceLine("1", new string('X', 20_000), "1", "szt.", "1.00", string.Empty,
        string.Empty, "1.00", string.Empty, "0.23", "23");
    var oversizedPages = InvoicePagePlanner.Plan([oversized]);
    Require(InvoicePagePlanner.EstimateLineHeight(oversized) <= 600,
        "Ekstremalnie długi opis może wyjść poza stronę A4.");
    Require(oversizedPages.SelectMany(page => page.Lines).Single() == oversized,
        "Paginacja zgubiła pozycję z bardzo długim opisem.");
}

static void TestStateContextIsolation()
{
    var attempt = DateTimeOffset.UtcNow.AddMinutes(-10);
    var apiBlockedUntil = DateTimeOffset.UtcNow.AddMinutes(3);
    var blockedUntil = DateTimeOffset.UtcNow.AddMinutes(5);
    var lastDownloadAttempt = DateTimeOffset.UtcNow.AddSeconds(-2);
    var state = new AppState
    {
        ContextNip = "5265877635",
        HistoryMonthsBack = 3,
        HistoricalBackfillBeforeIssueDate = new DateOnly(2026, 7, 1),
        LastSuccessfulSyncUtc = DateTimeOffset.UtcNow,
        PermanentStorageHwmDate = DateTimeOffset.UtcNow,
        ApiBlockedUntilUtc = apiBlockedUntil,
        InvoiceDownloadBlockedUntilUtc = blockedUntil,
        LastInvoiceDownloadAttemptUtc = lastDownloadAttempt,
        InvoiceDownloadAttemptsUtc = [attempt],
        Invoices = new Dictionary<string, StoredInvoice>(StringComparer.Ordinal)
        {
            ["OLD-KSEF-NUMBER"] = new StoredInvoice
            {
                KsefNumber = "OLD-KSEF-NUMBER",
                XmlDownloadFailureCount = 2,
                NextXmlDownloadAttemptUtc = blockedUntil
            }
        }
    };

    var snapshot = state.Snapshot();
    state.Invoices["OLD-KSEF-NUMBER"].SellerName = "Zmieniony sprzedawca";
    Require(string.IsNullOrEmpty(snapshot.Invoices["OLD-KSEF-NUMBER"].SellerName),
        "Migawka stanu współdzieli mutowalną fakturę z aktywnym cache.");
    Require(snapshot.HistoryMonthsBack == 3, "Migawka stanu zgubiła wersję zakresu historii.");
    Require(snapshot.HistoricalBackfillBeforeIssueDate == new DateOnly(2026, 7, 1),
        "Migawka stanu zgubiła znacznik uzupełniania historii.");
    Require(snapshot.InvoiceDownloadBlockedUntilUtc == blockedUntil,
        "Migawka stanu zgubiła czas blokady pobierania zwrócony przez KSeF.");
    Require(snapshot.ApiBlockedUntilUtc == apiBlockedUntil &&
            snapshot.LastInvoiceDownloadAttemptUtc == lastDownloadAttempt &&
            snapshot.Invoices["OLD-KSEF-NUMBER"].XmlDownloadFailureCount == 2 &&
            snapshot.Invoices["OLD-KSEF-NUMBER"].NextXmlDownloadAttemptUtc == blockedUntil,
        "Migawka stanu zgubiła globalny lub dokumentowy backoff API.");

    Require(!state.BindToContext("5265877635"), "Ponowne przypisanie tego samego NIP-u zmieniło cache.");
    Require(state.Invoices.Count == 1, "Cache został usunięty bez zmiany kontekstu.");
    Require(state.BindToContext("1234563218"), "Zmiana NIP-u nie została wykryta.");
    Require(state.Invoices.Count == 0, "Faktury poprzedniego NIP-u pozostały w cache.");
    Require(state.PermanentStorageHwmDate is null && state.LastSuccessfulSyncUtc is null,
        "Punkt synchronizacji poprzedniego NIP-u nie został wyzerowany.");
    Require(state.HistoricalBackfillBeforeIssueDate is null,
        "Zmiana NIP-u zachowała znacznik migracji poprzedniego kontekstu.");
    Require(state.InvoiceDownloadBlockedUntilUtc is null,
        "Zmiana NIP-u zachowała blokadę pobierania poprzedniego kontekstu.");
    Require(state.ApiBlockedUntilUtc == apiBlockedUntil,
        "Zmiana NIP-u usunęła globalną blokadę API i mogłaby ominąć Retry-After.");
    Require(state.InvoiceDownloadAttemptsUtc.SequenceEqual([attempt]),
        "Zmiana kontekstu usunęła licznik limitu pobierania API.");
}

static void TestMonthlyInvoiceSummary()
{
    var invoices = new[]
    {
        new StoredInvoice { GrossAmount = 1234.50m, Currency = "PLN" },
        new StoredInvoice { GrossAmount = 100.25m, Currency = "pln" },
        new StoredInvoice { GrossAmount = 90m, Currency = " EUR " },
        new StoredInvoice { GrossAmount = 10m, Currency = string.Empty }
    };

    var totals = MonthlyInvoiceSummary.CalculateGrossTotals(invoices);
    Require(totals.Count == 2, "Podsumowanie nie rozdzieliło walut.");
    Require(totals[0] == new CurrencyTotal("PLN", 1344.75m),
        "Podsumowanie nie znormalizowało lub nie zsumowało kwot PLN.");
    Require(totals[1] == new CurrencyTotal("EUR", 90m),
        "Podsumowanie nie obliczyło kwoty EUR.");
    var expectedPln = 1344.75m.ToString("N2", CultureInfo.GetCultureInfo("pl-PL"));
    Require(MonthlyInvoiceSummary.FormatGrossTotals(invoices) == $"Łącznie brutto: {expectedPln} PLN  •  90,00 EUR",
        "Podsumowanie nie używa polskiego formatu kwot.");
    Require(MonthlyInvoiceSummary.FormatGrossTotals(Array.Empty<StoredInvoice>()) == "Łącznie brutto: 0,00 PLN",
        "Pusty miesiąc nie ma jednoznacznego podsumowania.");
}

static void TestInvoiceAmountSortKey()
{
    decimal[] input = [-120m, -318m, -57.98m, 0m, 1000m, 1237.86m, 101.98m];
    var rows = input.Select(amount => new InvoiceRow
    {
        Source = new StoredInvoice { GrossAmount = amount, Currency = "PLN" }
    }).ToList();

    Require(rows.OrderBy(row => row.GrossAmountSortValue).Select(row => row.GrossAmountSortValue)
            .SequenceEqual(input.OrderBy(amount => amount)),
        "Klucz sortowania kwoty brutto nie sortuje wartości liczbowo rosnąco.");
    Require(rows.OrderByDescending(row => row.GrossAmountSortValue).Select(row => row.GrossAmountSortValue)
            .SequenceEqual(input.OrderByDescending(amount => amount)),
        "Klucz sortowania kwoty brutto nie sortuje wartości liczbowo malejąco.");

    var expected = $"{1000m.ToString("N2", CultureInfo.GetCultureInfo("pl-PL"))} PLN";
    Require(rows.Single(row => row.GrossAmountSortValue == 1000m).GrossAmount == expected,
        "Poprawka sortowania zmieniła polski format wyświetlanej kwoty.");
}

static void TestInvoiceNewState()
{
    var invoice = new StoredInvoice { KsefNumber = "KSEF-NEW", InvoiceNumber = "FV/NEW/1" };
    var row = new InvoiceRow { Source = invoice };
    Require(invoice.IsNew && row.IsNew && row.NewLabel == "NOWA",
        "Nowa faktura nie otrzymała spójnego oznaczenia w modelu wiersza.");

    var unopenedSnapshot = invoice.Snapshot();
    invoice.ViewedAtUtc = DateTimeOffset.UtcNow;
    Require(!invoice.IsNew && !row.IsNew && string.IsNullOrEmpty(row.NewLabel),
        "Oznaczenie NOWA nie zniknęło po pierwszym otwarciu faktury.");
    Require(unopenedSnapshot.IsNew,
        "Zmiana oryginalnej faktury zmodyfikowała wcześniejszą migawkę listy.");
    Require(!invoice.Snapshot().IsNew,
        "Nowa migawka nie zachowała informacji o obejrzeniu faktury.");
}

static void TestStatusBanner()
{
    var now = DateTimeOffset.Parse("2026-08-02T10:00:00Z", CultureInfo.InvariantCulture);
    var banner = new StatusBannerState(TimeSpan.FromSeconds(30));
    banner.Apply(new AppStatusMessage("Gotowy."), now);
    Require(!banner.Expire(now.AddHours(1)) && banner.Current?.Text == "Gotowy.",
        "Zwykły status został niepotrzebnie ukryty.");

    banner.Apply(new AppStatusMessage("Błąd", StatusSeverity.Error), now);
    Require(banner.Current?.IsError == true && banner.ExpiresAtUtc == now.AddSeconds(30),
        "Błąd na dolnej belce nie otrzymał czasu wygaśnięcia 30 sekund.");
    Require(!banner.Expire(now.AddSeconds(29.999)), "Błąd zniknął przed upływem 30 sekund.");

    banner.Apply(new AppStatusMessage("Nowszy błąd", StatusSeverity.Error), now.AddSeconds(20));
    Require(!banner.Expire(now.AddSeconds(30)) && banner.Current?.Text == "Nowszy błąd",
        "Stary termin wygaśnięcia usunął nowszy błąd.");
    Require(banner.Expire(now.AddSeconds(50)) && banner.Current is null,
        "Błąd nie zniknął po 30 sekundach.");

    banner.Apply(new AppStatusMessage("Jeszcze jeden błąd", StatusSeverity.Error), now);
    banner.Apply(new AppStatusMessage("Odświeżono poprawnie."), now.AddSeconds(5));
    Require(!banner.Expire(now.AddMinutes(1)) && banner.Current?.Text == "Odświeżono poprawnie.",
        "Termin starego błędu usunął nowszy komunikat informacyjny.");
}

static void TestUserFacingErrors()
{
    var unauthorized = UserFacingErrors.ForSynchronization(
        new KsefApiException("KSeF HTTP 401: techniczny sekret", HttpStatusCode.Unauthorized));
    Require(unauthorized.Contains("Sprawdź NIP i token", StringComparison.Ordinal) &&
            !unauthorized.Contains("HTTP", StringComparison.OrdinalIgnoreCase) &&
            !unauthorized.Contains("sekret", StringComparison.OrdinalIgnoreCase),
        "Komunikat logowania nie jest prosty albo ujawnia szczegóły techniczne.");

    var limited = UserFacingErrors.ForSynchronization(
        new KsefApiException("KSeF HTTP 429", HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.FromMinutes(7)));
    Require(limited.Contains("spróbuje ponownie", StringComparison.OrdinalIgnoreCase) &&
            limited.Contains("7 min", StringComparison.Ordinal),
        "Komunikat limitu KSeF nie wyjaśnia dalszego działania aplikacji.");

    var network = UserFacingErrors.ForSynchronization(new HttpRequestException("DNS lookup failed"));
    Require(network.Contains("internetem", StringComparison.OrdinalIgnoreCase) &&
            !network.Contains("DNS", StringComparison.OrdinalIgnoreCase),
        "Komunikat sieciowy pokazuje techniczny błąd DNS.");

    var fallback = UserFacingErrors.ForSynchronization(new InvalidOperationException("tajny techniczny szczegół"));
    Require(fallback.Contains("dzienniku", StringComparison.OrdinalIgnoreCase) &&
            !fallback.Contains("tajny", StringComparison.OrdinalIgnoreCase),
        "Komunikat ogólny ujawnia treść wyjątku.");

    var myDrLogin = UserFacingErrors.ForMyDrSynchronization(
        new MyDrApiException("techniczny sekret", HttpStatusCode.Unauthorized, "invalid_client"));
    Require(myDrLogin.Contains("Client ID", StringComparison.Ordinal) &&
            !myDrLogin.Contains("techniczny", StringComparison.OrdinalIgnoreCase),
        "Komunikat logowania MyDR nie jest prosty albo ujawnia szczegóły techniczne.");
    var myDrMalformed = UserFacingErrors.ForMyDrSynchronization(
        new MyDrApiException("MyDR nie zwrócił poprawnej kwoty brutto dla jednej z usług."));
    Require(myDrMalformed.Contains("Ostatnia poprawna kwota", StringComparison.Ordinal),
        "Komunikat niepełnych danych MyDR nie wyjaśnia zachowania ostatniej poprawnej sumy.");
    var myDrLimited = UserFacingErrors.ForMyDrSynchronization(
        new MyDrApiException("HTTP 429", HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.FromSeconds(31)));
    Require(myDrLimited.Contains("około minutę", StringComparison.Ordinal),
        "Komunikat limitu MyDR nie wyjaśnia użytkownikowi czasu bezpiecznego ponowienia.");
}

static void TestApplicationLog()
{
    var directory = Path.Combine(Path.GetTempPath(), $"ksef-monitor-log-test-{Guid.NewGuid():N}");
    var path = Path.Combine(directory, "app.log");
    try
    {
        var log = new ApplicationLog(path);
        log.Info("Test", "Pierwszy wpis");
        log.Error("Test", "Operacja nie powiodła się", new InvalidOperationException("pełny szczegół diagnostyczny"));
        var clientSecret = "TEST_ONLY_NOT_A_REAL_CLIENT_SECRET_" + Guid.NewGuid().ToString("N");
        var refreshToken = "TEST_ONLY_NOT_A_REAL_REFRESH_TOKEN_" + Guid.NewGuid().ToString("N");
        var accessToken = "TEST_ONLY_NOT_A_REAL_ACCESS_TOKEN_" + Guid.NewGuid().ToString("N");
        var ksefToken = "TEST_ONLY_NOT_A_REAL_KSEF_TOKEN_" + Guid.NewGuid().ToString("N");
        var bearerToken = "TEST_ONLY_NOT_A_REAL_BEARER_" + Guid.NewGuid().ToString("N");
        var authorizationValue = "Basic TEST_ONLY_NOT_A_REAL_AUTHORIZATION_" + Guid.NewGuid().ToString("N");
        var authorizationFormValue = "TEST_ONLY_NOT_A_REAL_AUTH_FORM_" + Guid.NewGuid().ToString("N");
        var githubToken = "gh" + "p_" + new string('G', 36);
        var jwt = "eyJ" + new string('A', 12) + "." + new string('B', 16) + "." + new string('C', 20);
        var privateKeyBody = "TEST_ONLY_NOT_A_REAL_PRIVATE_KEY_" + Guid.NewGuid().ToString("N");
        var privateKey = "-----BEGIN " + "PRIVATE KEY-----\n" + privateKeyBody + "\n-----END " + "PRIVATE KEY-----";

        log.Info("Redakcja JSON", $"{{\"client_secret\":\"{clientSecret}\",\"refresh_token\":\"{refreshToken}\"}}");
        log.Info("Redakcja formularza", $"access_token={accessToken}&token={ksefToken}");
        log.Info("Redakcja nagłówka", $"Authorization: {authorizationValue}");
        log.Info("Redakcja autoryzacji formularza", $"authorization=Bearer {authorizationFormValue}");
        log.Info("Redakcja Bearer", $"Odpowiedź zawiera Bearer {bearerToken}");
        log.Info("Redakcja formatów", $"GitHub {githubToken}; JWT {jwt}; {privateKey}");
        log.Info("Zwykłe słowo", "Ten token wygasł, ale w tym zdaniu nie ma jego wartości.");
        log.Error("Redakcja wyjątku", "Nie zapisuj sekretu z wyjątku",
            new InvalidOperationException($"KSeF token: {ksefToken}; refresh_token={refreshToken}"));
        Parallel.For(0, 20, index => log.Info("Równoległy test", $"Wpis {index}"));
        var text = log.ReadRecent();
        Require(text.Contains("[INFO] [Test] Pierwszy wpis", StringComparison.Ordinal), "Dziennik nie zachował wpisu informacyjnego.");
        Require(text.Contains("pełny szczegół diagnostyczny", StringComparison.Ordinal), "Dziennik zgubił techniczny opis wyjątku.");
        Require(Enumerable.Range(0, 20).All(index => text.Contains($"Wpis {index}", StringComparison.Ordinal)),
            "Równoległy zapis skleił lub zgubił wpisy dziennika.");
        var secretCanaries = new[]
        {
            clientSecret, refreshToken, accessToken, ksefToken, bearerToken, authorizationValue, authorizationFormValue,
            githubToken, jwt, privateKeyBody
        };
        Require(secretCanaries.All(secret => !text.Contains(secret, StringComparison.Ordinal)),
            "Dziennik ujawnił co najmniej jeden testowy sekret.");
        Require(text.Contains("[REDACTED]", StringComparison.Ordinal) &&
                text.Contains("[REDACTED GITHUB TOKEN]", StringComparison.Ordinal) &&
                text.Contains("[REDACTED JWT]", StringComparison.Ordinal) &&
                text.Contains("[REDACTED PRIVATE KEY]", StringComparison.Ordinal),
            "Dziennik nie oznaczył wszystkich obsługiwanych rodzajów sekretów.");
        Require(text.Contains("Ten token wygasł, ale w tym zdaniu nie ma jego wartości.", StringComparison.Ordinal),
            "Redakcja niepotrzebnie usunęła zwykłe użycie słowa token.");

        var rotatingPath = Path.Combine(directory, "rotating.log");
        var rotating = new ApplicationLog(rotatingPath, maximumFileBytes: 256);
        var rotatedSecret = "TEST_ONLY_NOT_A_REAL_ROTATED_SECRET_" + Guid.NewGuid().ToString("N");
        rotating.Info("Rotacja", $"client_secret={rotatedSecret} " + new string('A', 170));
        rotating.Info("Rotacja", new string('B', 170));
        Require(File.Exists(Path.Combine(directory, "rotating.previous.log")) && File.Exists(rotatingPath),
            "Dziennik nie ogranicza rozmiaru przez rotację pliku.");
        var rotatedText = rotating.ReadRecent();
        Require(rotatedText.Contains(new string('A', 30), StringComparison.Ordinal) &&
                rotatedText.Contains(new string('B', 30), StringComparison.Ordinal),
            "Podgląd dziennika nie łączy poprzedniego i bieżącego pliku po rotacji.");
        Require(!rotatedText.Contains(rotatedSecret, StringComparison.Ordinal) &&
                rotatedText.Contains("client_secret=[REDACTED]", StringComparison.Ordinal),
            "Sekret pozostał widoczny w poprzednim pliku po rotacji dziennika.");
    }
    finally
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}

static void TestMyDrStateAndSchedule()
{
    Require(MyDrVisitStateClassifier.IsPerformed("Do rozliczenia") &&
            MyDrVisitStateClassifier.IsPerformed("  oczekuje   na płatność ") &&
            MyDrVisitStateClassifier.IsPerformed("ZAKONCZONA") &&
            MyDrVisitStateClassifier.IsPerformed("Zamknięta") &&
            MyDrVisitStateClassifier.IsPerformed("Archiwalna"),
        "Klasyfikator MyDR nie rozpoznaje wszystkich obsługiwanych stanów wykonanej wizyty.");
    Require(!MyDrVisitStateClassifier.IsPerformed("Zaplanowana i opłacona") &&
            !MyDrVisitStateClassifier.IsPerformed("Anulowana"),
        "Klasyfikator MyDR uznał niewykonaną wizytę za wykonaną.");

    var connectionId = Guid.NewGuid();
    var state = new MyDrState
    {
        ConnectionId = connectionId,
        DoctorTurnoverSchemaVersion = MyDrState.CurrentDoctorTurnoverSchemaVersion,
        LastCheckLocalDate = new DateOnly(2026, 8, 2),
        ApiBlockedUntilUtc = DateTimeOffset.Parse("2026-08-02T12:30:00Z", CultureInfo.InvariantCulture),
        Months = new Dictionary<string, MyDrMonthSummary>(StringComparer.Ordinal)
        {
            ["2026-08"] = new MyDrMonthSummary
            {
                Year = 2026,
                Month = 8,
                GrossAmount = 123.45m,
                DoctorBreakdownAvailable = true,
                DoctorTurnovers =
                [
                    new MyDrDoctorTurnover
                    {
                        PersonnelId = 71,
                        DisplayName = "Anna Kowalska",
                        GrossAmount = 123.45m,
                        VisitCount = 1
                    }
                ]
            }
        },
        Visits = new Dictionary<long, MyDrCachedVisit>
        {
            [17] = new MyDrCachedVisit { VisitId = 17, VisitDate = new DateOnly(2026, 8, 2), GrossAmount = 123.45m }
        }
    };
    var snapshot = state.Snapshot();
    state.Months["2026-08"].GrossAmount = 0;
    state.Months["2026-08"].DoctorTurnovers[0].GrossAmount = 0;
    Require(snapshot.Months["2026-08"].GrossAmount == 123.45m,
        "Migawka MyDR współdzieli mutowalne podsumowanie ze stanem aktywnym.");
    Require(snapshot.Months["2026-08"].DoctorTurnovers.Single().GrossAmount == 123.45m,
        "Migawka MyDR współdzieli listę obrotów lekarzy ze stanem aktywnym.");
    Require(snapshot.ApiBlockedUntilUtc == DateTimeOffset.Parse("2026-08-02T12:30:00Z", CultureInfo.InvariantCulture),
        "Migawka MyDR zgubiła Retry-After API.");
    var roundTrip = JsonSerializer.Deserialize<MyDrState>(JsonSerializer.Serialize(snapshot))
                    ?? throw new InvalidOperationException("Nie odczytano nowego stanu MyDR po zapisie JSON.");
    roundTrip.NormalizeAfterLoad();
    Require(roundTrip.DoctorTurnoverSchemaVersion == MyDrState.CurrentDoctorTurnoverSchemaVersion &&
            roundTrip.Months["2026-08"].DoctorBreakdownAvailable &&
            roundTrip.Months["2026-08"].DoctorTurnovers.Single().DisplayName == "Anna Kowalska",
        "Nowy format stanu MyDR nie przechodzi pełnego cyklu zapis/odczyt.");
    Require(!state.BindToConnection(connectionId),
        "Ponowne przypięcie tego samego konta MyDR nie powinno zerować stanu.");
    Require(state.BindToConnection(Guid.NewGuid()),
        "Zmiana konta MyDR nie została rozpoznana.");
    Require(state.Months.Count == 0 && state.Visits.Count == 0 && state.LastCheckLocalDate is null &&
            state.ApiBlockedUntilUtc is null,
        "Zmiana konta MyDR nie wyczyściła danych poprzedniego połączenia.");

    var legacyJson = $$"""
        {
          "ConnectionId": "{{connectionId}}",
          "LastCheckLocalDate": "2026-08-02",
          "Months": {
            "2026-08": { "Year": 2026, "Month": 8, "GrossAmount": 321.00, "VisitCount": 2 }
          },
          "Visits": {}
        }
        """;
    var legacy = JsonSerializer.Deserialize<MyDrState>(legacyJson)
                 ?? throw new InvalidOperationException("Nie odczytano starego stanu MyDR.");
    legacy.NormalizeAfterLoad();
    Require(!legacy.Months["2026-08"].DoctorBreakdownAvailable &&
            legacy.Months["2026-08"].GrossAmount == 321m,
        "Migracja starego stanu MyDR utraciła sumę albo oznaczyła nieistniejący podział jako gotowy.");
    Require(legacy.UpgradeDoctorTurnoverSchema() && legacy.LastCheckLocalDate is null &&
            legacy.DoctorTurnoverSchemaVersion == MyDrState.CurrentDoctorTurnoverSchemaVersion,
        "Migracja nie zaplanowała jednorazowego pobrania podziału według lekarzy.");
    Require(!legacy.UpgradeDoctorTurnoverSchema(),
        "Migracja podziału lekarzy uruchamia się więcej niż jeden raz.");

    var unboundLegacy = JsonSerializer.Deserialize<MyDrState>(
        """{"Months":{"2026-08":{"GrossAmount":99.00}},"Visits":{"8":{"VisitId":8,"VisitDate":"2026-08-01"}}}""")
        ?? throw new InvalidOperationException("Nie odczytano niezwiązanego starego stanu MyDR.");
    unboundLegacy.NormalizeAfterLoad();
    Require(unboundLegacy.BindToConnection(Guid.Empty) &&
            unboundLegacy.Months.Count == 0 && unboundLegacy.Visits.Count == 0,
        "Stan MyDR bez identyfikatora konta pozostał na dysku mimo braku poświadczeń.");

    var zone = TimeZoneInfo.CreateCustomTimeZone("Test/Warsaw", TimeSpan.FromHours(2), "Test", "Test");
    var now = DateTimeOffset.Parse("2026-08-02T10:00:00Z", CultureInfo.InvariantCulture);
    Require(MyDrDailySchedule.GetNextCheckUtc(now, new DateOnly(2026, 8, 1), zone) == now,
        "Niewykonane dzisiejsze sprawdzenie MyDR nie zostało zaplanowane od razu.");
    Require(MyDrDailySchedule.GetNextCheckUtc(now, new DateOnly(2026, 8, 2), zone) ==
            DateTimeOffset.Parse("2026-08-02T22:05:00Z", CultureInfo.InvariantCulture),
        "Kolejne sprawdzenie MyDR nie zostało zaplanowane na następny dzień czasu warszawskiego.");

    var warsaw = MyDrDailySchedule.WarsawTimeZone;
    Require(MyDrDailySchedule.GetNextCheckUtc(
                DateTimeOffset.Parse("2026-01-15T12:00:00Z", CultureInfo.InvariantCulture),
                new DateOnly(2026, 1, 15),
                warsaw) == DateTimeOffset.Parse("2026-01-15T23:05:00Z", CultureInfo.InvariantCulture),
        "Zimowy harmonogram MyDR nie uwzględnia czasu CET w Warszawie.");
    Require(MyDrDailySchedule.GetNextCheckUtc(
                DateTimeOffset.Parse("2026-07-15T12:00:00Z", CultureInfo.InvariantCulture),
                new DateOnly(2026, 7, 15),
                warsaw) == DateTimeOffset.Parse("2026-07-15T22:05:00Z", CultureInfo.InvariantCulture),
        "Letni harmonogram MyDR nie uwzględnia czasu CEST w Warszawie.");
}

static void TestMyDrDoctorTurnovers()
{
    var sources = new[]
    {
        new MyDrVisitTurnoverSource(1, " Jan ", " Kowalski ", new DateOnly(2026, 8, 1), 11, 100m),
        new MyDrVisitTurnoverSource(1, "Jan", "Nowak", new DateOnly(2026, 8, 3), 12, -20m),
        new MyDrVisitTurnoverSource(1, null, null, new DateOnly(2026, 8, 4), 13, 0m),
        new MyDrVisitTurnoverSource(2, "Adam", "Nowak", new DateOnly(2026, 8, 2), 21, 80m),
        new MyDrVisitTurnoverSource(3, "Zero", "Salda", new DateOnly(2026, 8, 2), 31, 50m),
        new MyDrVisitTurnoverSource(3, "Zero", "Salda", new DateOnly(2026, 8, 3), 32, -50m),
        new MyDrVisitTurnoverSource(4, "Ewa", "Korekta", new DateOnly(2026, 8, 4), 41, -10m),
        new MyDrVisitTurnoverSource(5, " \t ", null, new DateOnly(2026, 8, 5), 51, 5m)
    };

    var result = MyDrDoctorTurnoverCalculator.Calculate(sources);
    Require(result.Select(item => item.PersonnelId).SequenceEqual([2L, 1L, 5L, 4L]),
        "Podział obrotu MyDR ma niepoprawne sortowanie albo połączył osoby o tym samym nazwisku.");
    Require(result.Single(item => item.PersonnelId == 1).DisplayName == "Jan Nowak" &&
            result.Single(item => item.PersonnelId == 1).VisitCount == 3,
        "Podział MyDR nie zachował najnowszej dostępnej nazwy albo liczby wizyt lekarza.");
    Require(result.Single(item => item.PersonnelId == 5).DisplayName == "Personel MyDR #5",
        "Brakująca nazwa osoby MyDR nie otrzymała bezpiecznego opisu zastępczego.");
    Require(result.All(item => item.PersonnelId != 3) && result.Any(item => item.GrossAmount < 0m),
        "Saldo równe zero nie zostało ukryte albo ujemna korekta została utracona.");
    Require(result.Sum(item => item.GrossAmount) == sources.Sum(item => item.GrossAmount),
        "Suma obrotów lekarzy różni się od miesięcznego obrotu MyDR.");

    var summary = new MyDrMonthSummary
    {
        Year = 2026,
        Month = 8,
        GrossAmount = result.Sum(item => item.GrossAmount),
        DoctorBreakdownAvailable = true,
        DoctorTurnovers = result.Select(item => item.Snapshot()).Reverse().ToList()
    };
    Require(summary.NormalizeDoctorTurnovers() &&
            summary.DoctorTurnovers.Select(item => item.PersonnelId).SequenceEqual([2L, 1L, 5L, 4L]),
        "Podział lekarzy nie został poprawnie znormalizowany po odczycie z dysku.");
    var visibleRows = MyDrDoctorTurnoverPresentation.GetVisibleRows(summary);
    Require(visibleRows.Select(item => item.PersonnelId).SequenceEqual([2L, 1L, 5L, 4L]) &&
            visibleRows.Any(item => item.GrossAmount < 0m) &&
            visibleRows.All(item => item.GrossAmount != 0m),
        "Warstwa prezentacji MyDR nie ukrywa dokładnie sald zerowych.");
    visibleRows[0].GrossAmount = 0m;
    Require(summary.DoctorTurnovers[0].GrossAmount != 0m,
        "Warstwa prezentacji zwróciła mutowalny element stanu MyDR.");
    Require(MyDrPersonnelName.FromParts("Anna\nMaria", " Nowak\tTest ") == "Anna Maria Nowak Test",
        "Normalizacja nazwy personelu skleja wyrazy rozdzielone białymi znakami.");

    var inconsistent = new MyDrMonthSummary
    {
        GrossAmount = 5m,
        DoctorBreakdownAvailable = true,
        DoctorTurnovers =
        [
            new MyDrDoctorTurnover { PersonnelId = 1, DisplayName = "A", GrossAmount = 5m }
        ]
    };
    Require(!inconsistent.NormalizeDoctorTurnovers() &&
            !inconsistent.DoctorBreakdownAvailable && inconsistent.DoctorTurnovers.Count == 0,
        "Niespójny podział lekarzy został pokazany jako poprawny.");

    var currentVisit = new MyDrVisit
    {
        Id = 91,
        DoctorId = 71,
        DoctorName = "Maria",
        DoctorSurname = "Lekarska",
        Date = "2026-08-09",
        State = "Zakończona",
        VisitKind = "Prywatna"
    };
    var cachedVisit = new MyDrCachedVisit
    {
        VisitId = 91,
        VisitDate = new DateOnly(2026, 8, 9),
        GrossAmount = 147.50m,
        ServiceCount = 2
    };
    var calculatedSummary = MyDrMonthSummaryCalculator.Calculate(
        2026,
        8,
        DateTimeOffset.Parse("2026-08-10T10:00:00Z", CultureInfo.InvariantCulture),
        [new MyDrPerformedVisitTurnover(currentVisit, cachedVisit.VisitDate, cachedVisit)]);
    Require(calculatedSummary.GrossAmount == 147.50m && calculatedSummary.VisitCount == 1 &&
            calculatedSummary.ServiceCount == 2 && calculatedSummary.DoctorBreakdownAvailable &&
            calculatedSummary.DoctorTurnovers.Single().PersonnelId == 71 &&
            calculatedSummary.DoctorTurnovers.Single().DisplayName == "Maria Lekarska",
        "Miesięczne podsumowanie nie połączyło aktualnej osoby wizyty z poprawną kwotą cache.");

    try
    {
        _ = MyDrMonthSummaryCalculator.Calculate(
            2026,
            8,
            DateTimeOffset.UtcNow,
            [new MyDrPerformedVisitTurnover(currentVisit, cachedVisit.VisitDate,
                new MyDrCachedVisit { VisitId = 92, VisitDate = cachedVisit.VisitDate, GrossAmount = 147.50m })]);
        throw new InvalidOperationException("Podsumowanie MyDR zaakceptowało cache innej wizyty.");
    }
    catch (InvalidOperationException exception)
    {
        Require(exception.Message.Contains("Cache wizyty", StringComparison.Ordinal),
            "Niezgodny cache MyDR nie zwrócił bezpiecznego komunikatu.");
    }

    var performedWithoutPerson = new MyDrVisit
    {
        Id = 93,
        DoctorId = null,
        Date = "2026-08-10",
        State = "Zakończona",
        VisitKind = "Prywatna"
    };
    try
    {
        _ = MyDrMonthSummaryCalculator.Calculate(
            2026,
            8,
            DateTimeOffset.UtcNow,
            [new MyDrPerformedVisitTurnover(
                performedWithoutPerson,
                new DateOnly(2026, 8, 10),
                new MyDrCachedVisit
                {
                    VisitId = 93,
                    VisitDate = new DateOnly(2026, 8, 10),
                    GrossAmount = 10m,
                    ServiceCount = 1
                })]);
        throw new InvalidOperationException("Podsumowanie MyDR zaakceptowało wykonaną wizytę bez personelu.");
    }
    catch (MyDrApiException exception)
    {
        Require(exception.Message.Contains("identyfikatora osoby", StringComparison.OrdinalIgnoreCase),
            "Wykonana wizyta bez personelu nie zwróciła bezpiecznego komunikatu.");
    }

    try
    {
        _ = MyDrDoctorTurnoverCalculator.Calculate(
            [new MyDrVisitTurnoverSource(0, "A", "B", new DateOnly(2026, 8, 1), 1, 1m)]);
        throw new InvalidOperationException("Agregator MyDR zaakceptował niepoprawny identyfikator personelu.");
    }
    catch (MyDrApiException exception)
    {
        Require(exception.Message.Contains("identyfikatora osoby", StringComparison.OrdinalIgnoreCase),
            "Błąd identyfikatora personelu MyDR nie jest bezpieczny ani czytelny.");
    }

    try
    {
        _ = MyDrDoctorTurnoverCalculator.Calculate(
        [
            new MyDrVisitTurnoverSource(1, "A", "B", new DateOnly(2026, 8, 1), 1, decimal.MaxValue),
            new MyDrVisitTurnoverSource(1, "A", "B", new DateOnly(2026, 8, 2), 2, 1m)
        ]);
        throw new InvalidOperationException("Agregator MyDR zaakceptował kwotę spoza zakresu decimal.");
    }
    catch (OverflowException)
    {
        // Oczekiwane: synchronizacja zachowa poprzednią poprawną migawkę.
    }
}

static void TestApiTrafficPolicies()
{
    var visit = new MyDrVisit
    {
        Id = 17,
        Date = "2026-08-02",
        State = "Do rozliczenia",
        LatestModification = "2026-08-02T12:00:00"
    };
    var cached = new MyDrCachedVisit
    {
        VisitId = 17,
        VisitDate = new DateOnly(2026, 8, 2),
        State = "Do rozliczenia",
        LatestModification = "2026-08-02T12:00:00",
        GrossAmount = 100m
    };
    Require(MyDrVisitCachePolicy.CanReuse(visit, new DateOnly(2026, 8, 2), cached),
        "Niezmieniona wizyta MyDR nie korzysta z cache i generowałaby zbędny GET usług.");
    Require(!MyDrVisitCachePolicy.CanReuse(visit, new DateOnly(2026, 8, 2), cached, forceRefresh: true),
        "Ręczne pełne odświeżenie MyDR nie omija cache wizyt.");
    visit.LatestModification = "2026-08-02T12:01:00";
    Require(!MyDrVisitCachePolicy.CanReuse(visit, new DateOnly(2026, 8, 2), cached),
        "Zmieniona wizyta MyDR błędnie korzysta z nieaktualnego cache.");
    visit.LatestModification = string.Empty;
    Require(!MyDrVisitCachePolicy.CanReuse(visit, new DateOnly(2026, 8, 2), cached),
        "Wizyta bez znacznika modyfikacji nie może bezpiecznie korzystać z cache.");

    Require(KsefXmlDownloadPolicy.ShouldStopQueue(HttpStatusCode.Unauthorized) &&
            KsefXmlDownloadPolicy.ShouldStopQueue(HttpStatusCode.Forbidden) &&
            KsefXmlDownloadPolicy.ShouldStopQueue(HttpStatusCode.InternalServerError) &&
            KsefXmlDownloadPolicy.ShouldStopQueue(HttpStatusCode.TooManyRequests) &&
            !KsefXmlDownloadPolicy.ShouldStopQueue(HttpStatusCode.NotFound),
        "Polityka kolejki XML nie rozróżnia awarii całego API od błędu pojedynczego dokumentu.");
    Require(KsefXmlDownloadPolicy.GetRetryDelay(1, HttpStatusCode.NotFound) == TimeSpan.FromMinutes(15) &&
            KsefXmlDownloadPolicy.GetRetryDelay(2, HttpStatusCode.NotFound) == TimeSpan.FromHours(1) &&
            KsefXmlDownloadPolicy.GetRetryDelay(3, HttpStatusCode.NotFound) == TimeSpan.FromHours(4) &&
            KsefXmlDownloadPolicy.GetRetryDelay(10, HttpStatusCode.NotFound) == TimeSpan.FromHours(24) &&
            KsefXmlDownloadPolicy.GetRetryDelay(1, HttpStatusCode.TooManyRequests, TimeSpan.FromHours(2)) ==
            TimeSpan.FromHours(2),
        "Backoff XML nie rośnie wykładniczo albo ignoruje Retry-After serwera.");

    var now = DateTimeOffset.UtcNow;
    var pendingInvoices = Enumerable.Range(1, 63)
        .Select(index => new StoredInvoice
        {
            KsefNumber = $"KSEF-{index}",
            DiscoveredAtUtc = now.AddMinutes(index),
            ViewedAtUtc = index == 1 ? null : now,
            NextXmlDownloadAttemptUtc = index == 2 ? now.AddHours(1) : null,
            Xml = index == 3 ? "<Faktura/>" : null
        })
        .ToList();
    var selected = KsefXmlDownloadPolicy.SelectPending(pendingInvoices, now, 60);
    Require(selected.Count == 60 && selected[0].KsefNumber == "KSEF-1" &&
            selected.All(invoice => invoice.KsefNumber is not ("KSEF-2" or "KSEF-3")),
        "Kolejka XML przekracza limit, ignoruje backoff albo nie daje pierwszeństwa nowej fakturze.");

    const int visitsPerDay = 20;
    const int days = 30;
    var previousDailyReloadRequests = visitsPerDay * days * (days + 1) / 2;
    var incrementalRequests = visitsPerDay * days;
    Require(previousDailyReloadRequests == 9300 && incrementalRequests == 600,
        "Test modelu obciążenia MyDR ma niepoprawne założenia.");
}

static void TestAtomicMyDrStateReplacement()
{
    var directory = Path.Combine(Path.GetTempPath(), "ksef-monitor-mydr-replace-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var path = Path.Combine(directory, "mydr-state.dat");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("POPRZEDNIE_KONTO"));
        File.WriteAllBytes(path + ".bak", Encoding.UTF8.GetBytes("BACKUP_POPRZEDNIEGO_KONTA"));
        File.WriteAllBytes(path + ".tmp", Encoding.UTF8.GetBytes("NIEDOKONCZONY_ZAPIS"));
        var replacement = Encoding.UTF8.GetBytes("NOWY_PUSTY_STAN");

        AppStore.AtomicReplaceWithoutBackup(path, replacement);

        Require(File.ReadAllBytes(path).SequenceEqual(replacement),
            "Atomowa zmiana konta MyDR nie zapisała nowego stanu.");
        Require(!File.Exists(path + ".bak") && !File.Exists(path + ".tmp"),
            "Po zmianie konta pozostała kopia lub plik tymczasowy danych poprzedniego konta MyDR.");
    }
    finally
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}

static async Task TestMyDrProtocolAsync()
{
    var clientId = "TEST_ONLY_CLIENT_" + Guid.NewGuid().ToString("N");
    var clientSecret = "TEST_ONLY_SECRET_" + Guid.NewGuid().ToString("N");
    var refreshToken = "TEST_ONLY_REFRESH_" + Guid.NewGuid().ToString("N");
    var rotatedRefreshToken = "TEST_ONLY_ROTATED_" + Guid.NewGuid().ToString("N");
    var accessToken = "TEST_ONLY_ACCESS_" + Guid.NewGuid().ToString("N");
    var tokenRequestCount = 0;
    var requestedPages = new List<int>();
    var serviceRequestCount = 0;
    string? immediatelyPersistedRefreshToken = null;

    using var handler = new DelegateHandler(async (message, cancellationToken) =>
    {
        Require(message.RequestUri?.Host == "edm.mydr.pl", "Klient MyDR nie używa produkcyjnego hosta.");
        var path = message.RequestUri?.AbsolutePath ?? string.Empty;
        if (path.EndsWith("/o/token/", StringComparison.Ordinal))
        {
            tokenRequestCount++;
            Require(message.Method == HttpMethod.Post, "OAuth MyDR nie używa POST.");
            var form = ParseForm(await message.Content!.ReadAsStringAsync(cancellationToken));
            Require(form.Count == 4 &&
                    form["grant_type"] == "refresh_token" &&
                    form["client_id"] == clientId &&
                    form["client_secret"] == clientSecret &&
                    form["refresh_token"] == refreshToken,
                "Żądanie OAuth MyDR nie zawiera dokładnie wymaganych danych.");
            return Json(HttpStatusCode.OK,
                $$"""{"expires_in":36000,"access_token":"{{accessToken}}","token_type":"Bearer","scope":"profile external_api","refresh_token":"{{rotatedRefreshToken}}","requires_2fa":false}""");
        }

        Require(message.Headers.Authorization?.Scheme == "Bearer" &&
                message.Headers.Authorization.Parameter == accessToken,
            "Zapytanie MyDR nie używa uzyskanego Bearer tokena.");
        if (path.EndsWith("/visits/", StringComparison.Ordinal))
        {
            var query = ParseForm((message.RequestUri?.Query ?? string.Empty).TrimStart('?'));
            Require(query["visit_kind"] == "Prywatna" && query["date_from"] == "2026-08-01" &&
                    query["date_to"] == "2026-08-31" && query["page_size"] == "100",
                "Lista wizyt MyDR nie używa poprawnego rodzaju lub zakresu dat.");
            var page = int.Parse(query["page"], CultureInfo.InvariantCulture);
            requestedPages.Add(page);
            return page == 1
                ? Json(HttpStatusCode.OK, """{"current_page":1,"last_page":2,"count":2,"next":"https://niezaufany.example/strona/2","results":[{"id":11,"doctor":"71","doctor_name":"Anna","doctor_surname":"Kowalska","date":"2026-08-02","state":"Do rozliczenia","visit_kind":"Prywatna","latest_modification":"2026-08-02T12:00:00"}]}""")
                : Json(HttpStatusCode.OK, """{"current_page":2,"last_page":2,"count":2,"next":null,"results":[{"id":12,"doctor":null,"date":"2026-08-03","state":"Zaplanowana","visit_kind":"Prywatna","latest_modification":"2026-08-02T13:00:00"}]}""");
        }

        if (path.EndsWith("/visits/11/services/", StringComparison.Ordinal))
        {
            serviceRequestCount++;
            return Json(HttpStatusCode.OK,
                """[{"id":"101","name":{"historyczny_format":true},"quantity":"2.00","base_price":100.00,"discount":10.00,"value":190.00},{"id":102,"insurer_service_code":12345,"quantity":1,"base_price":"50.00","discount":null,"value":"50.00"}]""");
        }

        if (path.EndsWith("/visits/12/services/", StringComparison.Ordinal))
        {
            serviceRequestCount++;
            return Json(HttpStatusCode.OK,
                """[{"id":201,"quantity":2,"base_price":"100.00","discount":"10.00","value":"190.00"}]""");
        }

        if (path.EndsWith("/visits/13/services/", StringComparison.Ordinal))
        {
            serviceRequestCount++;
            return Json(HttpStatusCode.OK,
                """{"count":1,"next":null,"results":[{"id":301,"value":"190.00"}]}""");
        }

        if (path.EndsWith("/visits/14/services/", StringComparison.Ordinal))
        {
            serviceRequestCount++;
            return Json(HttpStatusCode.OK,
                """[{"id":401,"name":"DANE_MEDYCZNE_TEST","value":"DANE_MEDYCZNE_TEST"}]""");
        }

        if (path.EndsWith("/visits/15/services/", StringComparison.Ordinal))
        {
            serviceRequestCount++;
            return Json(HttpStatusCode.OK, """[{"id":501,"value":null}]""");
        }

        if (path.EndsWith("/visits/16/services/", StringComparison.Ordinal))
        {
            serviceRequestCount++;
            return Json(HttpStatusCode.OK, """[{"id":601,"value":-10.50},{"id":602,"value":"-0.50"}]""");
        }

        if (path.EndsWith("/visits/17/services/", StringComparison.Ordinal))
        {
            serviceRequestCount++;
            return Json(HttpStatusCode.OK, "[]");
        }

        return Json(HttpStatusCode.NotFound, """{"detail":"Nieobsłużona ścieżka testowa"}""");
    });

    var credentials = new MyDrCredentials
    {
        ClientId = clientId,
        ClientSecret = clientSecret,
        RefreshToken = refreshToken
    };
    using var client = new MyDrApiClient(
        credentials,
        handler,
        rotated => immediatelyPersistedRefreshToken = rotated,
        TimeSpan.Zero);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var token = await client.AuthenticateAsync(timeout.Token);
    Require(tokenRequestCount == 1 && token.RotatedRefreshToken == rotatedRefreshToken &&
            immediatelyPersistedRefreshToken == rotatedRefreshToken &&
            client.TakeRotatedRefreshToken() == rotatedRefreshToken && client.TakeRotatedRefreshToken() is null,
        "Klient MyDR nie przekazał rotacji Refresh Tokena do natychmiastowego zapisu.");
    var visits = await client.GetPrivateVisitsAsync(new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31), timeout.Token);
    Require(requestedPages.SequenceEqual([1, 2]) && visits.Select(visit => visit.Id).SequenceEqual([11L, 12L]),
        "Klient MyDR nie obsłużył bezpiecznie paginacji lub filtra wizyt prywatnych.");
    Require(visits[0].DoctorId == 71 && visits[0].DoctorName == "Anna" && visits[0].DoctorSurname == "Kowalska" &&
            visits[1].DoctorId is null && !visits[1].IsPerformed,
        "Klient MyDR nie odczytał osoby realizującej albo odrzucił niewykonaną wizytę bez przypisanej osoby.");
    var services = await client.GetVisitServicesAsync(11, timeout.Token);
    Require(serviceRequestCount == 1 &&
            services.Select(service => service.Id).SequenceEqual([101L, 102L]) &&
            services.Sum(MyDrApiClient.GetServiceGrossValue) == 240m,
        "Klient MyDR nie obsłużył produkcyjnego wariantu liczba/tekst w polach usług.");
    var documentedServices = await client.GetVisitServicesAsync(12, timeout.Token);
    Require(documentedServices.Sum(MyDrApiClient.GetServiceGrossValue) == 190m,
        "Klient MyDR przestał obsługiwać udokumentowany tekstowy format kwot.");
    var corrections = await client.GetVisitServicesAsync(16, timeout.Token);
    Require(corrections.Sum(MyDrApiClient.GetServiceGrossValue) == -11m,
        "Klient MyDR nie zachował ujemnych wartości korekt.");
    var emptyServices = await client.GetVisitServicesAsync(17, timeout.Token);
    Require(emptyServices.Count == 0,
        "Klient MyDR nie zaakceptował poprawnej pustej listy usług.");

    try
    {
        _ = await client.GetVisitServicesAsync(13, timeout.Token);
        throw new InvalidOperationException("Klient MyDR zaakceptował nieudokumentowany obiekt zamiast tablicy usług.");
    }
    catch (MyDrApiException exception)
    {
        Require(exception.Message.Contains("otrzymano obiekt", StringComparison.Ordinal),
            "Błąd struktury usług nie wskazuje bezpiecznie rodzaju odpowiedzi.");
    }

    try
    {
        _ = await client.GetVisitServicesAsync(14, timeout.Token);
        throw new InvalidOperationException("Klient MyDR zaakceptował niepoprawną kwotę usługi.");
    }
    catch (MyDrApiException exception)
    {
        Require(!exception.Message.Contains("DANE_MEDYCZNE_TEST", StringComparison.Ordinal),
            "Wyjątek parsera MyDR ujawnił treść odpowiedzi medycznej.");
    }

    var missingValue = await client.GetVisitServicesAsync(15, timeout.Token);
    try
    {
        _ = MyDrApiClient.GetServiceGrossValue(missingValue.Single());
        throw new InvalidOperationException("Klient MyDR potraktował brak kwoty jako zero.");
    }
    catch (MyDrApiException exception)
    {
        Require(exception.Message.Contains("kwoty brutto", StringComparison.OrdinalIgnoreCase),
            "Brak kwoty usługi nie zwrócił bezpiecznego komunikatu.");
    }

    Require(tokenRequestCount == 1 && requestedPages.Count == 2 && serviceRequestCount == 7,
        "Klient MyDR wykonał nadmiarowe logowanie, stronę listy albo pobranie usług.");

}

static async Task TestMyDrRetryAndPacingAsync()
{
    var tokenRequests = 0;
    var visitOneRequests = 0;
    var visitTwoRequests = 0;
    var accessToken = string.Empty;
    var requestTimes = new List<DateTimeOffset>();

    using var handler = new DelegateHandler((message, _) =>
    {
        var path = message.RequestUri?.AbsolutePath ?? string.Empty;
        if (path.EndsWith("/o/token/", StringComparison.Ordinal))
        {
            tokenRequests++;
            accessToken = $"access-{tokenRequests}";
            return Task.FromResult(Json(HttpStatusCode.OK,
                $$"""{"expires_in":36000,"access_token":"{{accessToken}}","token_type":"Bearer","scope":"external_api","requires_2fa":false}"""));
        }

        Require(message.Headers.Authorization?.Parameter == accessToken,
            "Ponowione żądanie MyDR nie użyło nowego Access Tokena.");
        requestTimes.Add(DateTimeOffset.UtcNow);
        if (path.EndsWith("/visits/1/services/", StringComparison.Ordinal))
        {
            visitOneRequests++;
            return Task.FromResult(visitOneRequests == 1
                ? Json(HttpStatusCode.Unauthorized, "{}")
                : Json(HttpStatusCode.OK, "[]"));
        }

        if (path.EndsWith("/visits/2/services/", StringComparison.Ordinal))
        {
            visitTwoRequests++;
            var limited = Json(HttpStatusCode.TooManyRequests, "{}");
            limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(31));
            return Task.FromResult(limited);
        }

        return Task.FromResult(Json(HttpStatusCode.NotFound, "{}"));
    });

    var credentials = new MyDrCredentials
    {
        ClientId = "client",
        ClientSecret = "secret",
        RefreshToken = "refresh"
    };
    using var client = new MyDrApiClient(credentials, handler, minimumRequestInterval: TimeSpan.FromMilliseconds(40));
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await client.AuthenticateAsync(timeout.Token);
    _ = await client.GetVisitServicesAsync(1, timeout.Token);
    Require(tokenRequests == 2 && visitOneRequests == 2,
        "HTTP 401 MyDR nie wykonał dokładnie jednego odnowienia tokena i jednej ponownej próby.");

    try
    {
        _ = await client.GetVisitServicesAsync(2, timeout.Token);
        throw new InvalidOperationException("Klient MyDR zignorował HTTP 429.");
    }
    catch (MyDrApiException exception)
    {
        Require(exception.StatusCode == HttpStatusCode.TooManyRequests &&
                exception.RetryAfter is { TotalSeconds: >= 30 and <= 31 } &&
                visitTwoRequests == 1,
            "MyDR 429 został ponowiony albo utracono Retry-After.");
    }

    Require(requestTimes.Count == 3 &&
            requestTimes.Zip(requestTimes.Skip(1), (left, right) => right - left)
                .All(interval => interval >= TimeSpan.FromMilliseconds(30)),
        "Klient MyDR nie rozkłada kolejnych żądań API w czasie.");
}

static Dictionary<string, string> ParseForm(string value) => value
    .Split('&', StringSplitOptions.RemoveEmptyEntries)
    .Select(part => part.Split('=', 2))
    .ToDictionary(
        part => Uri.UnescapeDataString(part[0].Replace('+', ' ')),
        part => Uri.UnescapeDataString((part.Length > 1 ? part[1] : string.Empty).Replace('+', ' ')),
        StringComparer.Ordinal);

static async Task TestKsefProtocolAsync()
{
    using var rsa = RSA.Create(2048);
    var request = new CertificateRequest("CN=KSeF Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    var generator = X509SignatureGenerator.CreateForRSA(rsa, RSASignaturePadding.Pkcs1);
    using var certificate = request.Create(
        request.SubjectName,
        generator,
        DateTimeOffset.UtcNow.AddDays(-1),
        DateTimeOffset.UtcNow.AddDays(1),
        RandomNumberGenerator.GetBytes(16));
    var certificateBase64 = Convert.ToBase64String(certificate.Export(X509ContentType.Cert));
    var publicKeyId = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    const long timestampMs = 1785600000000;
    const string secret = "testowy-token-ksef";
    var querySeen = false;
    var invoiceDownloadSeen = false;
    var rateLimitNext = false;
    var badRequestNext = false;
    var missingHwmNext = false;
    var unauthorizedMetadataResponses = 0;
    HttpStatusCode? refreshFailureStatus = null;
    var expectedAccessToken = "access-token";
    var refreshSequence = 0;
    var requestCounts = new Dictionary<string, int>(StringComparer.Ordinal);
    var metadataRanges = new List<(DateTimeOffset From, DateTimeOffset? To, bool RestrictToHwm)>();
    var metadataRequestTimes = new List<DateTimeOffset>();
    var metadataProtocolReservations = 0;

    using var handler = new DelegateHandler(async (message, cancellationToken) =>
    {
        var path = message.RequestUri?.AbsolutePath ?? string.Empty;
        requestCounts[path] = requestCounts.GetValueOrDefault(path) + 1;
        if (path.EndsWith("/security/public-key-certificates", StringComparison.Ordinal))
            return Json(HttpStatusCode.OK, $$"""[{"certificate":"{{certificateBase64}}","publicKeyId":"{{publicKeyId}}","validFrom":"2025-01-01T00:00:00Z","validTo":"2030-01-01T00:00:00Z","usage":["KsefTokenEncryption"]}]""");

        if (path.EndsWith("/auth/challenge", StringComparison.Ordinal))
            return Json(HttpStatusCode.OK, $$"""{"challenge":"20260801-CR-TEST","timestamp":"2026-08-01T00:00:00Z","timestampMs":{{timestampMs}}}""");

        if (path.EndsWith("/auth/ksef-token", StringComparison.Ordinal))
        {
            using var body = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(cancellationToken));
            var encrypted = Convert.FromBase64String(body.RootElement.GetProperty("encryptedToken").GetString()!);
            var clear = Encoding.UTF8.GetString(rsa.Decrypt(encrypted, RSAEncryptionPadding.OaepSHA256));
            Require(clear == $"{secret}|{timestampMs}", "Niepoprawne szyfrowanie tokena KSeF.");
            Require(body.RootElement.GetProperty("contextIdentifier").GetProperty("value").GetString() == "5265877635", "Niepoprawny kontekst NIP.");
            Require(body.RootElement.GetProperty("publicKeyId").GetString() == publicKeyId, "Brak selektora publicKeyId.");
            return Json(HttpStatusCode.Accepted, """{"referenceNumber":"AUTH-REF","authenticationToken":{"token":"auth-token","validUntil":"2030-01-01T00:00:00Z"}}""");
        }

        if (path.EndsWith("/auth/AUTH-REF", StringComparison.Ordinal))
        {
            Require(message.Headers.Authorization?.Parameter == "auth-token", "Brak AuthenticationToken przy sprawdzeniu statusu.");
            return Json(HttpStatusCode.OK, """{"authenticationMethod":"Token","authenticationMethodInfo":{"category":"Token","code":"token","displayName":"Token"},"startDate":"2026-08-01T00:00:00Z","status":{"code":200,"description":"OK"}}""");
        }

        if (path.EndsWith("/auth/token/redeem", StringComparison.Ordinal))
        {
            Require(message.Headers.Authorization?.Parameter == "auth-token", "Brak AuthenticationToken przy redeem.");
            return Json(HttpStatusCode.OK, """{"accessToken":{"token":"access-token","validUntil":"2030-01-01T00:00:00Z"},"refreshToken":{"token":"refresh-token","validUntil":"2030-01-02T00:00:00Z"}}""");
        }

        if (path.EndsWith("/auth/token/refresh", StringComparison.Ordinal))
        {
            Require(message.Headers.Authorization?.Parameter == "refresh-token", "Brak RefreshToken przy odnowieniu sesji KSeF.");
            if (refreshFailureStatus is { } failureStatus)
            {
                refreshFailureStatus = null;
                var failure = Json(failureStatus, """{"detail":"Chwilowy błąd odnowienia"}""");
                if (failureStatus == HttpStatusCode.TooManyRequests)
                    failure.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(37));
                return failure;
            }
            expectedAccessToken = $"access-token-refreshed-{++refreshSequence}";
            return Json(HttpStatusCode.OK,
                JsonSerializer.Serialize(new
                {
                    accessToken = new { token = expectedAccessToken, validUntil = "2030-01-01T00:00:00Z" }
                }));
        }

        if (path.EndsWith("/invoices/query/metadata", StringComparison.Ordinal))
        {
            metadataRequestTimes.Add(DateTimeOffset.UtcNow);
            Require(message.Headers.Authorization?.Parameter == expectedAccessToken, "Brak aktualnego AccessToken przy pobieraniu metadanych.");
            if (unauthorizedMetadataResponses > 0)
            {
                unauthorizedMetadataResponses--;
                return Json(HttpStatusCode.Unauthorized, "{}");
            }
            using var body = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(cancellationToken));
            Require(body.RootElement.GetProperty("subjectType").GetString() == "Subject2", "Zapytanie nie dotyczy faktur otrzymanych.");
            var dateRange = body.RootElement.GetProperty("dateRange");
            Require(dateRange.GetProperty("dateType").GetString() == "PermanentStorage", "Zapytanie nie używa PermanentStorage.");
            var restrictToHwm = dateRange.GetProperty("restrictToPermanentStorageHwmDate").GetBoolean();
            var rangeFrom = dateRange.GetProperty("from").GetDateTimeOffset();
            var rangeTo = dateRange.TryGetProperty("to", out var toElement) ? toElement.GetDateTimeOffset() : (DateTimeOffset?)null;
            metadataRanges.Add((rangeFrom, rangeTo, restrictToHwm));
            querySeen = true;
            if (badRequestNext)
            {
                badRequestNext = false;
                return Json(HttpStatusCode.BadRequest, """
                {
                  "title":"Bad Request","status":400,"detail":"Żądanie jest nieprawidłowe.",
                  "errors":[{"code":21183,"description":"Zakres filtrowania wykracza poza dostępny zakres danych.",
                    "details":["Parametr dateRange.from jest późniejszy niż PermanentStorageHwmDate."]}]
                }
                """);
            }
            if (rateLimitNext)
            {
                rateLimitNext = false;
                var limited = Json(HttpStatusCode.TooManyRequests, """{"detail":"Limit żądań"}""");
                limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(42));
                return limited;
            }
            if (missingHwmNext)
            {
                missingHwmNext = false;
                return Json(HttpStatusCode.OK, """{"hasMore":false,"isTruncated":false,"invoices":[]}""");
            }
            return Json(HttpStatusCode.OK, """
            {
              "hasMore":false,"isTruncated":false,"permanentStorageHwmDate":"2026-08-01T12:00:00Z",
              "invoices":[{
                "ksefNumber":"5265877635-20260801-TEST","invoiceNumber":"FV/TEST/1","issueDate":"2026-08-01",
                "invoicingDate":"2026-08-01T10:00:00Z","acquisitionDate":"2026-08-01T10:01:00Z","permanentStorageDate":"2026-08-01T10:02:00Z",
                "seller":{"nip":"1111111111","name":"Test Sp. z o.o."},"buyer":{"identifier":{"type":"Nip","value":"5265877635"},"name":"Nabywca"},
                "netAmount":100.00,"vatAmount":23.00,"grossAmount":123.00,"currency":"PLN","invoiceType":"Vat",
                "formCode":{"systemCode":"FA (3)","schemaVersion":"1-0E","value":"FA"},"invoicingMode":"Online","hasAttachment":false,"isSelfInvoicing":false,"invoiceHash":"abc"
              }]
            }
            """);
        }

        if (path.EndsWith("/invoices/ksef/5265877635-20260801-TEST", StringComparison.Ordinal))
        {
            Require(message.Headers.Authorization?.Parameter == "access-token", "Brak AccessToken przy pobieraniu XML faktury.");
            invoiceDownloadSeen = true;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<Faktura><Fa><P_2>FV/TEST/1</P_2></Fa></Faktura>", Encoding.UTF8, "application/xml")
            };
        }

        return Json(HttpStatusCode.NotFound, """{"detail":"Nieobsłużona ścieżka testowa"}""");
    });

    var settings = new AppSettings { Nip = "5265877635" };
    using var client = new KsefApiClient(
        settings,
        secret,
        handler,
        metadataRequestInterval: TimeSpan.Zero,
        metadataRequestReservation: _ =>
        {
            metadataProtocolReservations++;
            return Task.CompletedTask;
        });
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await client.AuthenticateAsync(timeout.Token);
    Require(CountRequests(requestCounts, "/security/public-key-certificates") == 1 &&
            CountRequests(requestCounts, "/auth/challenge") == 1 &&
            CountRequests(requestCounts, "/auth/ksef-token") == 1 &&
            CountRequests(requestCounts, "/auth/AUTH-REF") == 1 &&
            CountRequests(requestCounts, "/auth/token/redeem") == 1,
        "Pełne uwierzytelnienie KSeF wykonało nadmiarową liczbę żądań.");
    await client.VerifyInvoiceReadAccessAsync(timeout.Token);
    Require(metadataRanges.Count == 1 && !metadataRanges[0].RestrictToHwm,
        "Test uprawnienia InvoiceRead może fałszywie zwrócić błąd HWM 21183.");
    metadataRanges.Clear();
    var result = await client.QueryReceivedInvoicesAsync(
        DateTimeOffset.Parse("2026-07-01T00:00:00+02:00", CultureInfo.InvariantCulture),
        timeout.Token);
    Require(querySeen, "Nie wysłano zapytania o metadane.");
    Require(result.Invoices.Count == 1, "Nie odczytano metadanych z API.");
    Require(result.Invoices[0].SellerName == "Test Sp. z o.o.", "Nie odczytano nazwy sprzedawcy z API.");
    Require(result.Invoices[0].GrossAmount == 123.00m, "Nie odczytano kwoty brutto z API.");
    var downloadedXml = await client.DownloadInvoiceXmlAsync("5265877635-20260801-TEST", timeout.Token);
    Require(invoiceDownloadSeen && downloadedXml.Contains("FV/TEST/1", StringComparison.Ordinal),
        "Klient nie pobrał pełnego XML faktury po numerze KSeF.");

    metadataRanges.Clear();
    var longRangeFrom = DateTimeOffset.UtcNow.AddMonths(-3);
    var longRangeResult = await client.QueryReceivedInvoicesAsync(longRangeFrom, timeout.Token);
    Require(metadataRanges.Count == 2, "Zakres dłuższy niż limit KSeF nie został podzielony na dwa okna.");
    Require(metadataRanges.All(range => range.RestrictToHwm), "Synchronizacja nie ogranicza wszystkich okien do stabilnego HWM.");
    Require(metadataRanges[0].From == longRangeFrom && metadataRanges[0].To == longRangeFrom.AddMonths(2),
        "Pierwsze okno metadanych nie ma bezpiecznej długości dwóch miesięcy.");
    Require(metadataRanges[0].To is { } firstWindowEnd &&
            metadataRanges[1].From == firstWindowEnd && metadataRanges[1].To is null,
        "Okna metadanych nie są przylegające lub ostatnie okno ma zbędną datę końcową.");
    Require(longRangeResult.Invoices.Count == 1, "Deduplikacja faktur na granicach okien nie działa.");

    var fullAuthRequestsBeforeRefreshFailure = CountFullKsefAuthenticationRequests(requestCounts);
    var refreshRequestsBefore = CountRequests(requestCounts, "/auth/token/refresh");
    var metadataRequestsBefore = CountRequests(requestCounts, "/invoices/query/metadata");
    unauthorizedMetadataResponses = 1;
    refreshFailureStatus = HttpStatusCode.TooManyRequests;
    try
    {
        await client.QueryReceivedInvoicesAsync(DateTimeOffset.UtcNow.AddMinutes(-5), timeout.Token);
        throw new InvalidOperationException("Klient KSeF zignorował 429 podczas odnowienia tokena.");
    }
    catch (KsefApiException exception)
    {
        Require(exception.StatusCode == HttpStatusCode.TooManyRequests &&
                exception.RetryAfter is { TotalSeconds: >= 36 and <= 37 },
            "Odnowienie KSeF utraciło HTTP 429 lub Retry-After.");
    }
    Require(CountRequests(requestCounts, "/auth/token/refresh") == refreshRequestsBefore + 1 &&
            CountRequests(requestCounts, "/invoices/query/metadata") == metadataRequestsBefore + 1 &&
            CountFullKsefAuthenticationRequests(requestCounts) == fullAuthRequestsBeforeRefreshFailure,
        "Błąd 429 refresh tokena uruchomił zbędne pełne logowanie KSeF.");

    // Po chwilowym błędzie refresh token pozostaje użyteczny; następna próba
    // wymaga dokładnie jednego refresh i jednego żądania docelowego.
    refreshRequestsBefore = CountRequests(requestCounts, "/auth/token/refresh");
    metadataRequestsBefore = CountRequests(requestCounts, "/invoices/query/metadata");
    _ = await client.QueryReceivedInvoicesAsync(DateTimeOffset.UtcNow.AddMinutes(-5), timeout.Token);
    Require(CountRequests(requestCounts, "/auth/token/refresh") == refreshRequestsBefore + 1 &&
            CountRequests(requestCounts, "/invoices/query/metadata") == metadataRequestsBefore + 1,
        "Ciepłe odnowienie KSeF wykonało więcej niż jedno żądanie refresh lub metadata.");

    refreshRequestsBefore = CountRequests(requestCounts, "/auth/token/refresh");
    metadataRequestsBefore = CountRequests(requestCounts, "/invoices/query/metadata");
    unauthorizedMetadataResponses = 1;
    _ = await client.QueryReceivedInvoicesAsync(DateTimeOffset.UtcNow.AddMinutes(-5), timeout.Token);
    Require(CountRequests(requestCounts, "/auth/token/refresh") == refreshRequestsBefore + 1 &&
            CountRequests(requestCounts, "/invoices/query/metadata") == metadataRequestsBefore + 2,
        "HTTP 401 KSeF nie wykonał dokładnie jednego refresh i jednej ponownej próby.");

    refreshRequestsBefore = CountRequests(requestCounts, "/auth/token/refresh");
    metadataRequestsBefore = CountRequests(requestCounts, "/invoices/query/metadata");
    unauthorizedMetadataResponses = 2;
    try
    {
        _ = await client.QueryReceivedInvoicesAsync(DateTimeOffset.UtcNow.AddMinutes(-5), timeout.Token);
        throw new InvalidOperationException("Klient KSeF wykonał więcej niż jedną ponowną próbę po 401.");
    }
    catch (KsefApiException exception)
    {
        Require(exception.StatusCode == HttpStatusCode.Unauthorized,
            "Drugi HTTP 401 KSeF nie został zwrócony do harmonogramu.");
    }
    Require(CountRequests(requestCounts, "/auth/token/refresh") == refreshRequestsBefore + 1 &&
            CountRequests(requestCounts, "/invoices/query/metadata") == metadataRequestsBefore + 2,
        "Klient KSeF ponowił żądanie więcej niż jeden raz po 401.");

    fullAuthRequestsBeforeRefreshFailure = CountFullKsefAuthenticationRequests(requestCounts);
    unauthorizedMetadataResponses = 1;
    refreshFailureStatus = HttpStatusCode.ServiceUnavailable;
    try
    {
        _ = await client.QueryReceivedInvoicesAsync(DateTimeOffset.UtcNow.AddMinutes(-5), timeout.Token);
        throw new InvalidOperationException("Klient KSeF zignorował 503 podczas odnowienia tokena.");
    }
    catch (KsefApiException exception)
    {
        Require(exception.StatusCode == HttpStatusCode.ServiceUnavailable,
            "Odnowienie KSeF utraciło HTTP 503.");
    }
    Require(CountFullKsefAuthenticationRequests(requestCounts) == fullAuthRequestsBeforeRefreshFailure,
        "Błąd 503 refresh tokena uruchomił zbędne pełne logowanie KSeF.");

    badRequestNext = true;
    try
    {
        await client.QueryReceivedInvoicesAsync(DateTimeOffset.UtcNow.AddMinutes(-5), timeout.Token);
        throw new InvalidOperationException("Klient zignorował odpowiedź HTTP 400.");
    }
    catch (KsefApiException exception)
    {
        Require(exception.StatusCode == HttpStatusCode.BadRequest, "Nie zachowano kodu HTTP 400.");
        Require(exception.HasErrorCode(21183), "Nie odczytano kodu 21183 z Problem Details.");
        Require(exception.Message.Contains("Parametr dateRange.from", StringComparison.Ordinal),
            "Komunikat zgubił właściwe szczegóły błędu KSeF.");
        Require(!exception.Message.EndsWith("Żądanie jest nieprawidłowe.", StringComparison.Ordinal),
            "Komunikat nadal pokazuje wyłącznie ogólny opis Problem Details.");
    }

    rateLimitNext = true;
    try
    {
        await client.QueryReceivedInvoicesAsync(DateTimeOffset.UtcNow.AddMinutes(-5), timeout.Token);
        throw new InvalidOperationException("Klient zignorował odpowiedź HTTP 429.");
    }
    catch (KsefApiException exception)
    {
        Require(exception.StatusCode == HttpStatusCode.TooManyRequests, "Nie zachowano kodu HTTP 429.");
        Require(exception.RetryAfter is { TotalSeconds: >= 41 and <= 42 }, "Nie odczytano nagłówka Retry-After.");
    }

    missingHwmNext = true;
    try
    {
        _ = await client.QueryReceivedInvoicesAsync(DateTimeOffset.UtcNow.AddMinutes(-5), timeout.Token);
        throw new InvalidOperationException("Klient KSeF zaakceptował metadane bez punktu HWM.");
    }
    catch (KsefApiException exception)
    {
        Require(exception.Message.Contains("niepełną odpowiedź", StringComparison.OrdinalIgnoreCase),
            "Brak HWM nie został zgłoszony jako niepełna odpowiedź KSeF.");
    }

    Require(metadataProtocolReservations == CountRequests(requestCounts, "/invoices/query/metadata"),
        "Nie każde fizyczne żądanie metadanych KSeF, w tym retry po 401, zarezerwowało osobny limit.");

    metadataRequestTimes.Clear();
    expectedAccessToken = "access-token";
    var metadataReservations = 0;
    using var pacingClient = new KsefApiClient(
        settings,
        secret,
        handler,
        metadataRequestInterval: TimeSpan.FromMilliseconds(40),
        metadataRequestReservation: _ =>
        {
            metadataReservations++;
            return Task.CompletedTask;
        });
    using var pacingTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await pacingClient.AuthenticateAsync(pacingTimeout.Token);
    await pacingClient.VerifyInvoiceReadAccessAsync(pacingTimeout.Token);
    await pacingClient.VerifyInvoiceReadAccessAsync(pacingTimeout.Token);
    Require(metadataReservations == 2 && metadataRequestTimes.Count == 2 &&
            metadataRequestTimes[1] - metadataRequestTimes[0] >= TimeSpan.FromMilliseconds(30),
        "Klient KSeF nie rezerwuje lub nie rozkłada kolejnych zapytań o metadane w czasie.");
}

static int CountRequests(IReadOnlyDictionary<string, int> counts, string pathSuffix) =>
    counts.Where(pair => pair.Key.EndsWith(pathSuffix, StringComparison.Ordinal)).Sum(pair => pair.Value);

static int CountFullKsefAuthenticationRequests(IReadOnlyDictionary<string, int> counts) =>
    CountRequests(counts, "/security/public-key-certificates") +
    CountRequests(counts, "/auth/challenge") +
    CountRequests(counts, "/auth/ksef-token") +
    CountRequests(counts, "/auth/AUTH-REF") +
    CountRequests(counts, "/auth/token/redeem");

static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
{
    Content = new StringContent(json, Encoding.UTF8, "application/json")
};

sealed class DelegateHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

    public DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) => _handler = handler;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        _handler(request, cancellationToken);
}
