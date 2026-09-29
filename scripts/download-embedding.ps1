$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$workspace = Split-Path -Parent $PSScriptRoot
$modelDirectory = Join-Path $workspace 'models/embedding'
New-Item -ItemType Directory -Force -Path $modelDirectory | Out-Null
$revision = '1110a243fdf4706b3f48f1d95db1a4f5529b4d41'
$base = "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/$revision"
$files = @(
    @{Name='model.onnx'; Remote='onnx/model_qint8_avx2.onnx'; Hash='4278337fd0ff3c68bfb6291042cad8ab363e1d9fbc43dcb499fe91c871902474'},
    @{Name='vocab.txt'; Remote='vocab.txt'; Hash='07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3'}
)
foreach ($file in $files) {
    $destination = Join-Path $modelDirectory $file.Name
    if (-not (Test-Path -LiteralPath $destination) -or (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.Hash) {
        Invoke-WebRequest -UseBasicParsing -Uri "$base/$($file.Remote)" -OutFile $destination
    }
    if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.Hash) { throw "Model checksum mismatch: $($file.Name)" }
}
Write-Host 'Verified the pinned ONNX INT8 model and BERT vocabulary. Model license: Apache-2.0.'
