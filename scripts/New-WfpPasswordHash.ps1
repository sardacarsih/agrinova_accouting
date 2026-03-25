param(
    [Parameter(Mandatory = $true)]
    [string]$Password,
    [int]$Iterations = 120000
)

$salt = New-Object byte[] 16
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
try {
    $rng.GetBytes($salt)
}
finally {
    $rng.Dispose()
}

$derive = [System.Security.Cryptography.Rfc2898DeriveBytes]::new(
    $Password,
    $salt,
    $Iterations,
    [System.Security.Cryptography.HashAlgorithmName]::SHA256)

try {
    $hash = $derive.GetBytes(32)
    $encodedSalt = [Convert]::ToBase64String($salt)
    $encodedHash = [Convert]::ToBase64String($hash)
    "pbkdf2-sha256`$$Iterations`$$encodedSalt`$$encodedHash"
}
finally {
    $derive.Dispose()
}
