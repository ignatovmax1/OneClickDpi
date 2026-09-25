using System;
using System.Security.Cryptography;
using System.Text;

var rsa = RSA.Create(2048);
var spki = rsa.ExportSubjectPublicKeyInfo();
var b64 = Convert.ToBase64String(spki);

// Format as PEM with ECDSA header (compatible with the app's reader)
var pem = "-----BEGIN ECDSA PUBLIC KEY-----\r\n" +
          $"{b64.Substring(0, 64)}\r\n{b64.Substring(64)}\r\n" +
          "-----END ECDSA PUBLIC KEY-----\r\n";

Console.Write(pem);
