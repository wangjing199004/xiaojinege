param([string]$TemplateDirectory = "")
$ErrorActionPreference = 'Stop'
$repoPath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($TemplateDirectory)) { $TemplateDirectory = Join-Path $repoPath '目录' }
$source = Get-Content -LiteralPath (Join-Path $repoPath 'src/Common/Infrastructure/HpDirectoryTemplates.cs') -Raw -Encoding utf8
$match = [regex]::Match($source, 'Material\s*=\s*"([^"]+)"')
if (-not $match.Success) { throw 'HP template key material not found.' }
$material = [Convert]::FromBase64String($match.Groups[1].Value)
$encryptionKey = New-Object byte[] 32
$authenticationKey = New-Object byte[] 32
[Array]::Copy($material, 0, $encryptionKey, 0, 32)
[Array]::Copy($material, 32, $authenticationKey, 0, 32)
$output = Join-Path $repoPath 'resources/hp-directory'
New-Item -ItemType Directory -Path $output -Force | Out-Null
try {
    foreach ($sheet in @(@('A3目录', 'sheet-a.enc'), @('A4目录', 'sheet-b.enc'), @('A0+图框', 'std-a0.enc'), @('A1+图框', 'std-a1.enc'), @('A2+图框', 'std-a2.enc'), @('封面', 'std-cover.enc'), @('目录签章框', 'std-seal.enc'))) {
        $plain = [IO.File]::ReadAllBytes((Join-Path $TemplateDirectory ($sheet[0] + '.dwg')))
        $aes = [Security.Cryptography.Aes]::Create()
        $aes.Key = $encryptionKey
        $aes.GenerateIV()
        $aes.Mode = [Security.Cryptography.CipherMode]::CBC
        $aes.Padding = [Security.Cryptography.PaddingMode]::PKCS7
        $encryptor = $aes.CreateEncryptor()
        $hmac = New-Object Security.Cryptography.HMACSHA256 -ArgumentList (,$authenticationKey)
        try {
            $cipher = $encryptor.TransformFinalBlock($plain, 0, $plain.Length)
            $payload = [Text.Encoding]::ASCII.GetBytes('XJGHP001') + $aes.IV + $cipher
            $tag = $hmac.ComputeHash([byte[]]$payload)
            [IO.File]::WriteAllBytes((Join-Path $output $sheet[1]), [byte[]]($payload + $tag))
        }
        finally {
            [Array]::Clear($plain, 0, $plain.Length)
            $encryptor.Dispose()
            $hmac.Dispose()
            $aes.Dispose()
        }
    }
}
finally {
    [Array]::Clear($material, 0, $material.Length)
    [Array]::Clear($encryptionKey, 0, $encryptionKey.Length)
    [Array]::Clear($authenticationKey, 0, $authenticationKey.Length)
}
Write-Output 'HP templates encrypted. Rebuild the three CAD projects to embed the updated assets.'
