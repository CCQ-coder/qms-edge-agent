param(
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [string]$Password = "kst-mes-mock",
    [string]$IpAddress = "192.168.1.71"
)

$certificateIp = $null
if (-not [System.Net.IPAddress]::TryParse($IpAddress, [ref]$certificateIp) -or
    $certificateIp.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork -or
    $certificateIp.Equals([System.Net.IPAddress]::Any)) {
    throw "IpAddress must be a specific IPv4 address."
}

$rsa = [System.Security.Cryptography.RSA]::Create(2048)
try {
    $request = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
        "CN=$IpAddress",
        $rsa,
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $san = [System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
    $san.AddIpAddress($certificateIp)
    $request.CertificateExtensions.Add($san.Build())
    $request.CertificateExtensions.Add(
        [System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
    $keyUsage = [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature -bor
        [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyEncipherment
    $request.CertificateExtensions.Add(
        [System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new($keyUsage, $true))
    $request.CertificateExtensions.Add(
        [System.Security.Cryptography.X509Certificates.X509SubjectKeyIdentifierExtension]::new($request.PublicKey, $false))
    $oids = [System.Security.Cryptography.OidCollection]::new()
    $null = $oids.Add([System.Security.Cryptography.Oid]::new("1.3.6.1.5.5.7.3.1"))
    $request.CertificateExtensions.Add(
        [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($oids, $false))
    $certificate = $request.CreateSelfSigned([DateTimeOffset]::Now.AddMinutes(-5), [DateTimeOffset]::Now.AddYears(3))
    try {
        [System.IO.File]::WriteAllBytes(
            $OutputPath,
            $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $Password))
        $certificatePath = [System.IO.Path]::ChangeExtension($OutputPath, ".cer")
        [System.IO.File]::WriteAllBytes(
            $certificatePath,
            $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
    }
    finally {
        $certificate.Dispose()
    }
}
finally {
    $rsa.Dispose()
}
