//-----------------------------------------------------------------------
// <copyright file="Program.cs" company="ParanoidMike">
//     Copyright (c) ParanoidMike. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

using System;
using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using System.Security.Permissions;
using CommandLine;
using ParanoidMike;

// TODO: restrict this down to just PathDiscovery & Write for the user's LOCALAPPDATA folder
[assembly:FileIOPermission(SecurityAction.RequestMinimum, AllLocalFiles = FileIOPermissionAccess.AllAccess)]
[assembly:RegistryPermission(SecurityAction.RequestMinimum, ViewAndModify = @"HKEY_CURRENT_USER\Software\Microsoft\Windows NT\CurrentVersion\EFS\CurrentKeys")]

// TODO: expand these permissions when EFS cert archiving is enabled
[assembly:StorePermission(SecurityAction.RequestMinimum, OpenStore = true, EnumerateCertificates = true)]

namespace EFSConfiguration
{
    /// <summary>
    /// The base class for this application.
    /// </summary>
    public class Program
    {
        #region Variables

        /// <summary>
        /// Internal set of variables to gather parsed user input from supplied command-line parameters.
        /// </summary>
        private static Arguments arguments;

        // TODO: Certificate template name should be returned in a form that is either:
        //   (a) directly useable in an expression examining a field of the digital certificate, or 
        //   (b) useable in more than one context (e.g. if there was some other Registry setting that recorded a form of the cert template)

        /// <summary>
        /// The name of the Certificate Template whose certificates will be used to update the user's EFS configuration.
        /// </summary>
        private static string certificateTemplateName; // TODO: Create a function that takes the friendly name of a cert template, determines if there's a connection to AD, and looks up the OID for that template

        /// <summary>
        /// Used to store whatever exit code will be sent to StdOut.
        /// </summary>
        private static int exitCode;

        /// <summary>
        /// Tracks whether the user's current EFS configuration (i.e. CertificateHash Registry value) is already populated with an acceptable certificate.
        /// </summary>
        private static bool isCurrentUserEfsConfigurationOK;

        /// <summary>
        /// The specific identifier to force the tool to only select EFS certificates that are issued from the specified Certificate Authority (CA).
        /// </summary>
        private static string issuingCAIdentifier; // TODO: enable this argument to receive an identifier of a targeted CA (e.g. Subject field's "CN", Serial number)

        /// <summary>
        /// Tracks whether the user's EFS configuration (i.e. CertificateHash Registry value) has been updated by this application.
        /// </summary>
        private static bool isUserEfsConfigurationUpdated;

        /// <summary>
        /// Enables the user to limit the chosen EFS certificate to only those enrolled with a v2 Template.
        /// </summary>
        private static bool migrateV1Certificates;

        #endregion

        #region Public Methods

        /// <summary>
        /// <para>
        /// Purpose of this application: automate the migration of a user's current EFS certificate from a self-signed
        /// EFS certificate to a CA-issued EFS certificate.  This will ensure that the organization has the ability to 
        /// recover the user's private key in the unlikely event that the user's private key gets deleted, the hard disk
        /// fails or the user's Profile becomes unavailable.
        /// </para>
        /// <para>
        /// This supports a recovery process that is complementary to the more traditional approach of recovering EFS 
        /// files using the Data Recovery Agent keys defined through Group Policy.
        /// </para>
        /// </summary>
        /// <param name="args">
        /// The command-line arguments specified at runtime of the process.
        /// </param>
        public static void Main(string[] args)
        {
            // Setup a trace log for capturing information on what the application is doing
            Utility.AddTraceLog(
                "EFSCertConfigUpdate", 
                "EFSCertConfigUpdateTraceLog.txt");

            // Write the date & time to the trace log
            Trace.WriteLine(
                "Current EFS cert update session started at " +
                DateTime.Now.ToString() +
                Environment.NewLine);

            // If the process has no parameters specified, then operate without; otherwise, parse those parameters
            if (args.Length > 0)
            {
                ParseArguments(args);
            }

            // This variable represents the certificate selected by this application to configure as the active EFS certificate
            X509Certificate2 efsCertificateToUse = new X509Certificate2();
            /* 
             * I found out the hard way that the new Constructor above doesn't initialize the X509Certificate2 object with a null value. 
             * Without this redundant code to set it explicitly to null, checking the value of EFSCertificateToUse without an assigned cert 
             * leads to a CryptographicException.
             */
            efsCertificateToUse = null;

            X509Store myStore = CertificateFunctions.OpenUserMyStore();
            
            /* 
             * Create a collection to enumerate the existing Certs in the MY store, and perform a Cast.
             * (Note to self: I don't know if the following code is casting _from_ or _to_ the myStore.Certificates collection.)
             */
            X509Certificate2Collection userCerts = (X509Certificate2Collection) myStore.Certificates;
            
            /* 
             * There are two potential approaches for finding the CA-issued EFS certificate(s):
             * 
             * 1. Find all certificates that contain the EFS EKU, then examine those certificates for issuer and/or Certificate 
             * Template.  Examining the issuer will let us find self-signed certificates and optionally archive them; examining 
             * the Certificate Template field will let us find the cert(s) issued by the target CA (where presumably key escrow 
             * has been performed).
             * 
             * 2. Find all the certificate(s) enrolled for a specified Certificate Template.
             * 
             * Unfortunately there is no measure on the client that will definitively indicate the cert and its keys are currently 
             * in the Keys Archive, but we usually make the assumption that any cert enrolled from a key-archival-enabled Cert Template 
             * had in fact had its keys archived (i.e. this is a success criteria for any enrollment from a Key Archival-required 
             * cert template).  If a cert was issued from a Key Archival-required cert template, it is likely a reasonable enough 
             * approximation of the desired state "my cert's private key is currently archived in the CA's database".
             */        

            // This is a very elegant method to narrow the user's certificates down to just the EFS certificates; unfortunately, it doesn't work
            X509Certificate2Collection userCertsWithEku = (X509Certificate2Collection) userCerts.Find(X509FindType.FindByExtension, "Enhanced Key Usage", true);

            /* 
             * Iterate through each user Certificate in this collection to 
             *   (a) identify an EFS cert that is valid for our purpose, 
             *   (b) determine whether the selected certificate matches the current EFS configuration, and if not,
             *   (c) update the EFS configuration.
             */

            foreach (X509Certificate2 x509Cert in userCertsWithEku)
            {
                Trace.WriteLine(
                    "Certificate being examined:" + Environment.NewLine + 
                    "     Friendly Name: " + x509Cert.FriendlyName + Environment.NewLine +
                    "     Subject:       " + x509Cert.Subject + Environment.NewLine +
                    "     Thumbprint:    " + x509Cert.GetCertHashString() + Environment.NewLine);

                // Check the enrolled template version, if "migratev1" was specified at runtime
                // Stop processing this certificate if it is NOT enrolled from a v2 certificate template, as that means 
                //   there was no opportunity for a Microsoft CA to archive the keypair during enrollment
                if ((migrateV1Certificates == true) && (CertificateFunctions.IsCertificateEnrolledFromV2Template(x509Cert) == false))
                {
                    Trace.WriteLine(
                        "Certificate Rejected: certificate is not enrolled from v2 certificate." + 
                        Environment.NewLine);
                    
                    continue;
                }

                // Stop processing this certificate if it is NOT Valid, so that the user doesn't end up encrypting files using an expired certificate
                else if (CertificateFunctions.IsCertificateValid(x509Cert) == false)
                {
                    Trace.WriteLine(
                        "Certificate Rejected: certificate is not a valid digital certificate." + 
                        Environment.NewLine);
                    continue;
                }

                // Determine whether this certificate was enrolled from the specified Certificate Template
                // If this command-line parameter is not passed in, then we should skip this check
                else if (certificateTemplateName != null)
                {
                    if (CertificateFunctions.IsCertificateEnrolledFromSpecifiedTemplate(x509Cert, certificateTemplateName) == false)
                    {
                        Trace.WriteLine(
                            "Certificate Rejected: certificate is not enrolled from specified cert template." + 
                            Environment.NewLine);
                        continue;
                    }
                }

                // TODO: re-enable this code once the CA-identifying argument is re-enabled
                //// If cert isn't self-signed, then it was issued by a Certificate Authority, but it could've been issued potentially by any CA
                //// Skip the cert if it was NOT issued by the intended CA
                ////else if (issuingCAIdentifier != null)
                ////{
                ////    (IsCertificateIssuedByIntendedCA(x509Cert, issuingCAIdentifier) == false)
                ////    {
                ////        continue;
                ////    }
                ////}

                // Skip the cert if it is self-signed, since self-signed certificates can never automatically be archived by a 
                // Windows Server CA, and are not the focus of this application.                
                else if (CertificateFunctions.IsCertificateSelfSigned(x509Cert) == true)
                {
                    Trace.WriteLine(
                        "Certificate Rejected: certificate is self-signed." + 
                        Environment.NewLine);
                    continue;
                }

                // Skip the cert if it DOES NOT contain the EFS EKU
                else if (EFSCertificateFunctions.IsCertificateAnEfsCertificate(x509Cert) == false)
                {
                    Trace.WriteLine(
                        "Certificate Rejected: certificate does not contain the EFS EKU." + 
                        Environment.NewLine);
                    continue;
                }

                // Skip the cert if the user does NOT have a Private Key for this certificate (i.e. ensure that they didn't just accidently 
                // or unintentionally import an EFS certificate without its private key)
                else if (x509Cert.HasPrivateKey == false)
                {
                    Trace.WriteLine(
                        "Certificate Rejected: certificate has no matching private key." + 
                        Environment.NewLine);
                    continue;
                }

                /* 
                 * Determine whether the selected certificate is the currently configured EFS certificate.
                 * If so, then no further certificate action is necessary -- not for updating the user's CertificateHash 
                 * registry setting at least.
                 * 
                 * NOTE: this function should be the 2nd-to-last function in this foreach loop, so that we're only bailing 
                 * out of the loop if we've ensured that the cert meets all criteria AND happens to be the currently-configured cert.
                 */

                    // TODO: in a future version, where all certs are evaluated and the best one chosen, this function may not be necessary
                else if (EFSCertificateFunctions.IsCertificateTheCurrentlyConfiguredEFSCertificate(x509Cert))
                {
                    Trace.WriteLine(
                        "The selected certificate is already configured as the active EFS certificate." + 
                        Environment.NewLine);

                    isCurrentUserEfsConfigurationOK = true;

                    // TODO: in a future version, where all certs are evaluated and the best one chosen, remove this "break" statement
                    // Stop checking any additional certificates, as the EFS cert currently in use has met all criteria
                    break;
                }
                else
                {
                    // This certificate is the candidate for use as the active EFS certificate
                    efsCertificateToUse = x509Cert;

                    Trace.WriteLine(
                        "Certificate Accepted: \"" + 
                        x509Cert.FriendlyName + 
                        "\", serial number " + 
                        x509Cert.SerialNumber + 
                        "." + 
                        Environment.NewLine);

                    // Exit the loop to stop checking any additional certificates
                    /* 
                     * TODO: Now that we've selected a candidate certificate, we'll stop examining other certificates - just in case 
                     * there are two or more valid certs from the same Template. In the future, we'll continue examining all certs, and
                     * create an array of candidate certs from which we'll select the best one, and potentially archive the rest.
                     */
                    break;
                }

                /* 
                 * TODO: investigate implementing an advanced mode whereby this application doesn't just select the first matching certificate,
                 * but picks the "best" of those certs.  For example, the "best" of all matching certs could be the one with the latest
                 * "Valid From:" date, one (if any) that was issued from a v2 certificate template, and the one that has the longest RSA key.
                 * 
                 * TODO: (v3 or 4 of this app) If multiple certs were available, then Archive all other matching certificates.
                 */
            }

            // If the foreach loop has identified an EFS certificate, then update the user's EFS Configuration with the selected certificate
            if (isCurrentUserEfsConfigurationOK != true && efsCertificateToUse != null)
            {
                // TODO: add this detail to Trace Logging
                ////Console.WriteLine("The original EFS CertificateHash registry setting was one value" + Environment.NewLine); // + certificateHashValue + Environment.NewLine);
                ////Console.WriteLine("The new EFS CertificateHash setting will be something else" + Environment.NewLine); // + certificateThumbprint + Environment.NewLine);

                isUserEfsConfigurationUpdated = EFSCertificateFunctions.UpdateUserEfsConfiguration(efsCertificateToUse);
            }

            // Were any suitable certificates identified?  If not, then indicate that no suitable certificates were found
            if (isUserEfsConfigurationUpdated != true  && isCurrentUserEfsConfigurationOK != true)
            {
                // TODO: send an error code to StdErr, for those IT admins that want to use this utility in a script (and prefer StdErr as a way to capture issues)
                // TODO: v2 - implement an Application Event Log message as well - try this sample code: http://www.thescarms.com/dotnet/EventLog.aspx
                Trace.WriteLine(
                    "Perhaps the user has no EFS certificates suitable for updating their EFS configuration - please notify the administrator." +
                    Environment.NewLine);

                exitCode = 1;

               /* 
                * TODO: (v3 or v4) The application could attempt to enroll a cert from the desired cert template, since no suitable certs were found.
                * If no such cert could be enrolled, then write an error to the Application Event Log.
                * 
                * Once this application implements the logic to enroll a suitable certificate, remove the reference to stdErr.
                */
            }

            // If the user ultimately succeeded in configuring a suitable EFS certificate, then exitCode=0; otherwise, it should be some non-zero value.
            if (isUserEfsConfigurationUpdated || isCurrentUserEfsConfigurationOK)
            {
                exitCode = 0;
            }

            if (isUserEfsConfigurationUpdated == true)
            {
               /* 
                * TODO: (v3) Assuming the application has updated the CertificateHash Registry value with the thumbprint from the selected EFS cert, 
                * the application could optionally deal with updating all encrypted files - either by notifying the user 
                * that they should run the EFS Assistant tool or CIPHER.EXE /U, or by running one of these tools in the background.
                */
            }

            // Now that all certificate store operations have completed, close the Handle to the user's MY store.
            myStore.Close();

            // TODO: report (Trace log, Application Event Log, non-zero Exit Code) if the user has no suitable certs but has CertificateHash configured (and report whether
            //       that configured cert is available in the user's cert store, is valid, and has a private key).

            // Close the Trace Log before exiting
            Utility.DisposeTraceLog();

            // Lastly, terminate the application
            System.Environment.Exit(exitCode);
        }

        #endregion

        #region Private Methods

        // TODO: remove this function once the Help text (/?) for this application has all the useful text from here.
        private static void DisplayUsage()
        {
            {
                // Get the name of the process executable, so that updates to the process name are automatically mirrored
                Process process = System.Diagnostics.Process.GetCurrentProcess();
                string processName = process.ProcessName;
                string processNameUpperCase = processName.ToUpper();

                Console.WriteLine("Updates your EFS configuration to use a centrally-managed EFS certificate." + Environment.NewLine);
                Console.WriteLine("" + Environment.NewLine);
                Console.WriteLine("  " + processNameUpperCase + " [argument1]" + Environment.NewLine);
                Console.WriteLine("" + Environment.NewLine);
                Console.WriteLine("  " + processNameUpperCase + " [argument1] [argument2]" + Environment.NewLine);
                Console.WriteLine("" + Environment.NewLine);
                Console.WriteLine("      [argument1] specifies the name of the desired Certificate Template" + Environment.NewLine);
                Console.WriteLine("                   e.g. \"Company EFS certificate version 2\"" + Environment.NewLine);
                Console.WriteLine("" + Environment.NewLine);
                Console.WriteLine("      [argument2] specifies the distinguished name of the Issuing CA" + Environment.NewLine);
                Console.WriteLine("                   e.g. \"IssuingCA01\"" + Environment.NewLine);
                Console.WriteLine("" + Environment.NewLine);
                Console.WriteLine("  Used without parameters, " + processNameUpperCase + " will select the first non-self-" + Environment.NewLine);
                Console.WriteLine("  signed EFS certificate it finds in the user's personal certificate store." + Environment.NewLine);
                Console.WriteLine("" + Environment.NewLine);
            }
        }

        /// <summary>
        /// Parses all command-line parameters that were supplied as arguments to the running process.
        /// </summary>
        /// <param name="args">
        /// The arguments supplied to the running process.
        /// </param>
        private static void ParseArguments(string[] args)
        {
            arguments = new Arguments();

            // TODO: re-implement this if the IsValid() function can be made to work when only one argument is present
            ////if (Parser.ParseArgumentsWithUsage(args, arguments) && arguments.IsValid())
            if (Parser.ParseArgumentsWithUsage(args, arguments))
            {
                // This variable should be set only if the matching parameter was specified at runtime
                if (!string.IsNullOrEmpty(arguments.CertificateTemplateName))
                {
                    certificateTemplateName = arguments.CertificateTemplateName;
                    Trace.WriteLine(
                        "The command line parameter 'template' was successfully parsed; template name \"" +
                        certificateTemplateName +
                        "\" was captured." +
                        Environment.NewLine);
                }

                // This variable should be set only if the matching parameter was specified at runtime
                if (!string.IsNullOrEmpty(arguments.IssuingCAIdentifier))
                {
                    issuingCAIdentifier = arguments.IssuingCAIdentifier;
                    Trace.WriteLine(
                        "The command line parameter 'issuingca' was successfully parsed; Certificate Authority name \"" +
                        issuingCAIdentifier +
                        "\" was captured." +
                        Environment.NewLine);
                }

                // This argument has a default value (false), so this variable can always safely be set
                migrateV1Certificates = arguments.MigrateV1Certs;

                if (migrateV1Certificates)
                {
                    Trace.WriteLine(
                        "The command line parameter 'migratev1' was successfully parsed; value of \"" +
                        migrateV1Certificates.ToString() +
                        "\" was captured." +
                        Environment.NewLine);
                }
            }
        }

        #endregion
    }
}
