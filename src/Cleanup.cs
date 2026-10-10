// Take the virtual controller driver back off the PC: every virtual controller, the driver, its
// settings, and the certificate the driver was signed with on this PC

using System.Security.Cryptography.X509Certificates;
using HIDMaestro;

namespace HhlGamepadWlan;

static class Cleanup
{
    const string CertificateSubject = "CN=HIDMaestroTestCert";

    public static void RemoveControllerDriver()
    {
        // Devices, orphans, driver packages and HKLM\Software\HIDMaestro
        HMContext.RemoveAllVirtualControllers();

        foreach (var store in new[] { StoreName.My, StoreName.Root, StoreName.TrustedPublisher })
        {
            using var s = new X509Store(store, StoreLocation.LocalMachine);
            s.Open(OpenFlags.ReadWrite);
            foreach (var cert in s.Certificates.Find(X509FindType.FindBySubjectDistinguishedName, CertificateSubject, false))
                s.Remove(cert);
        }

        // The driver files it unpacked
        foreach (var dir in Directory.GetDirectories(Path.GetTempPath(), "HIDMaestro*"))
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
