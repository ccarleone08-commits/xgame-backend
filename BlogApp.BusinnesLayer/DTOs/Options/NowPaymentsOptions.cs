namespace BlogApp.BusinnesLayer.DTOs.Options;

public class NowPaymentsOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string IpnSecret { get; set; } = string.Empty;
    public string AuthEmail { get; set; } = string.Empty;
    public string AuthPassword { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.nowpayments.io/v1/";
    public string IpnCallbackUrl { get; set; } = string.Empty;
    public string SuccessUrl { get; set; } = string.Empty;
    public string CancelUrl { get; set; } = string.Empty;
    public int MinimumAmountCacheMinutes { get; set; } = 10;
    public string[] SupportedPayCurrencies { get; set; } =
    [
        "btc",
        "eth",
        "usdt",
        "usdttrc20",
        "usdterc20",
        "usdtbsc",
        "usdtmatic",
        "usdtsol",
        "usdc",
        "usdcmatic",
        "usdcsol",
        "tusd",
        "tusdtrc20",
        "dai",
        "usddtrc20",
        "busd",
        "trx",
        "ltc",
        "doge",
        "xmr",
        "xno",
        "bnbbsc",
        "bnb",
        "maticmainnet",
        "sol",
        "xrp",
        "xlm",
        "ada",
        "ton",
        "bch",
        "dash",
        "vet",
        "uni",
        "dgb",
        "dot",
        "atom",
        "near",
        "algo",
        "zec",
        "xvg",
        "zen",
        "etc",
        "shib",
        "link",
        "avaxc"
    ];
}
