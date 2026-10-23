namespace CellRelay;
static class QrProfile
{
    public static ConnectionProfile Read(Bitmap image)
    {
        if (image.Width > 8192 || image.Height > 8192) throw new FormatException("QR 이미지는 8192픽셀 이하로 준비하세요.");
        var reader = new ZXing.Windows.Compatibility.BarcodeReader { AutoRotate = true, Options = new ZXing.Common.DecodingOptions { TryHarder = true, PossibleFormats = new[] { ZXing.BarcodeFormat.QR_CODE } } };
        var result = reader.Decode(image);
        if (result is null) throw new FormatException("QR 코드를 찾지 못했습니다. 아이폰 연결 QR 전체가 보이는 이미지를 선택하세요.");
        return ConnectionProfile.Parse(result.Text);
    }
}
