using System;
using System.Net;
using System.Net.Http;
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
Require(parsed.Fields.Count > 10, "Nie spłaszczono wszystkich pól XML.");
TestGrossLineVariant();
TestPefUblLineVariant();
Require(NipValidator.IsValid("526-587-76-35"), "Walidacja poprawnego NIP-u nie działa.");
Require(!NipValidator.IsValid("5265877634"), "Walidacja błędnego NIP-u nie działa.");
await TestKsefProtocolAsync();

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
    Require(line.VatRate == "23", "Nie odczytano stawki VAT pozycji PEF/UBL.");
}

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

    using var handler = new DelegateHandler(async (message, cancellationToken) =>
    {
        var path = message.RequestUri?.AbsolutePath ?? string.Empty;
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

        if (path.EndsWith("/invoices/query/metadata", StringComparison.Ordinal))
        {
            Require(message.Headers.Authorization?.Parameter == "access-token", "Brak AccessToken przy pobieraniu metadanych.");
            using var body = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(cancellationToken));
            Require(body.RootElement.GetProperty("subjectType").GetString() == "Subject2", "Zapytanie nie dotyczy faktur otrzymanych.");
            Require(body.RootElement.GetProperty("dateRange").GetProperty("dateType").GetString() == "PermanentStorage", "Zapytanie nie używa PermanentStorage.");
            querySeen = true;
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

        return Json(HttpStatusCode.NotFound, """{"detail":"Nieobsłużona ścieżka testowa"}""");
    });

    var settings = new AppSettings { Environment = KsefEnvironment.Test, Nip = "5265877635" };
    using var client = new KsefApiClient(settings, secret, handler);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await client.AuthenticateAsync(timeout.Token);
    var result = await client.QueryReceivedInvoicesAsync(DateTimeOffset.Parse("2026-07-01T00:00:00+02:00"), timeout.Token);
    Require(querySeen, "Nie wysłano zapytania o metadane.");
    Require(result.Invoices.Count == 1, "Nie odczytano metadanych z API.");
    Require(result.Invoices[0].SellerName == "Test Sp. z o.o.", "Nie odczytano nazwy sprzedawcy z API.");
    Require(result.Invoices[0].GrossAmount == 123.00m, "Nie odczytano kwoty brutto z API.");
}

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
