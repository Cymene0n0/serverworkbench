using System;
using System.Security.Cryptography;
using System.Text;
using ServerWorkbench.Core;

namespace ServerWorkbench.Desktop
{
 internal sealed class DpapiSecretProtector : ISecretProtector
 {
  private static readonly byte[] Entropy=Encoding.UTF8.GetBytes("ServerWorkbench/frp/token/v1");
  public string Protect(string plaintext)
  {
   if(plaintext==null)throw new ArgumentNullException(nameof(plaintext));
   var bytes=Encoding.UTF8.GetBytes(plaintext);
   try{return Convert.ToBase64String(ProtectedData.Protect(bytes,Entropy,DataProtectionScope.CurrentUser));}
   finally{Array.Clear(bytes,0,bytes.Length);}
  }
  public string Unprotect(string protectedText)
  {
   var bytes=Convert.FromBase64String(protectedText??"");byte[] clear=null;
   try{clear=ProtectedData.Unprotect(bytes,Entropy,DataProtectionScope.CurrentUser);return Encoding.UTF8.GetString(clear);}
   finally{Array.Clear(bytes,0,bytes.Length);if(clear!=null)Array.Clear(clear,0,clear.Length);}
  }
 }
}
