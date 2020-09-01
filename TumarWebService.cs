using System;
using System.Web;
using System.Web.Services;
using System.Web.Services.Protocols;
using System.Xml.Linq;
using System.Text;
using System.IO;
using System.Collections.Generic;
using System.Xml;
using SDFO.Crypto.TumarCrypto.Xml;
using SDFO.Crypto.Enums;
using SDFO.Crypto.TumarCrypto.Services;
using SDFO.Crypto.TumarCrypto.Helper;
using System.Timers;
using System.Diagnostics;
using Communicator.OracleConnector;

[WebService(Namespace = "TumarWebService")]
[WebServiceBinding(ConformsTo = WsiProfiles.BasicProfile1_1)]
public class Service : System.Web.Services.WebService
{
    Settings settings = new Settings();
    Fasti2 fasti2;

    public Service()
    {
        settings.Load();
        fasti2 = new Fasti2(settings);
    }

    [WebMethod]
    public string AddEvent(string sEvent)
    {
        int result = 0;
        try
        {
            string sSource = "AML";
            string sLog = "Application";

            if (!EventLog.SourceExists(sSource))
                EventLog.CreateEventSource(sSource, sLog);
            EventLog.WriteEntry(sSource, sEvent, EventLogEntryType.Information);
            result = 1;
        }
        finally { }
        XDocument answer = new XDocument(
            new XDeclaration("1.0", Encoding.GetEncoding("UTF-8").HeaderName, "yes"),
            new XElement("Result", result)
        );

        return answer.ToString();
    }

    [WebMethod]
    public string SetDocument(string xml, string sender, string receiver, string fasti2sender, string fasti2receiver, string cspProfile, string documentType)
    {
        OracleCredentials oracleCredentials = new OracleCredentials();
        oracleCredentials.Database = settings.DatabaseServer;
        oracleCredentials.User = settings.DatabaseUsername;
        oracleCredentials.Password = settings.DatabaseUserPassword;

        OracleConnectionClass oracle = new OracleConnectionClass(oracleCredentials);

        Logger.setOracleConnector(oracle);
      
        settings.CspProfile = cspProfile;

        string result = "0";

        string id = "";
        string file = "";
        string error = "";
        XmlDocument signed = null;
        string reference = "", confirmation = "";
        int errorCode = 0;
    
        try
        {
            XmlDocument document = new XmlDocument();
            document.LoadXml(xml);
            signed = SignDocument(document, sender, receiver);

            id = signed.DocumentElement["SignedData"]["Data"]["Root"]["DocumentUniqueIdentifier"].InnerText;
            file = documentType + id + ".xml";
            result = "1";

            signed.Save(settings.OutXmlDir + file);
        }
        catch (ArgumentException)
        {
            error = "Неверный формат XML файла";
            Logger.Add(LoggerEventsType.ERROR, "Неверный формат XML файла "+ id);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Logger.Add(ex);
        }

        if (!String.IsNullOrEmpty(error)) errorCode = -1;

        if (errorCode != -1) 
        {
            error = fasti2.SendMessage(fasti2sender, fasti2receiver, settings.CspProfile,
                                       /* Ref */  out reference,
                                       /* Conf */ out confirmation);

            if (!String.IsNullOrEmpty(error)) errorCode = -1;
        }

        XDocument answer = new XDocument(
            new XDeclaration("1.0", Encoding.GetEncoding("UTF-8").HeaderName, "yes"),
            new XElement("root",
                new XElement("Result", result),
                new XElement("DocumentUniqueIdentifier", id),
                new XElement("FileName", file),
                new XElement("Reference", reference),
                new XElement("Confirmation", confirmation),
                new XElement("Error", error),
                new XElement("ErrorCode", errorCode),
                new XElement("SignedXml", XElement.Parse(signed.OuterXml))
            )
        );

        return answer.ToString();
    }

    [WebMethod]
    public String GetAnswers(string receiver, string cspProfile)
    {
        string file_name_err = "";

        try
        {
            OracleCredentials oracleCredentials = new OracleCredentials();
            oracleCredentials.Database = settings.DatabaseServer;
            oracleCredentials.User = settings.DatabaseUsername;
            oracleCredentials.Password = settings.DatabaseUserPassword;

            OracleConnectionClass oracle = new OracleConnectionClass(oracleCredentials);

            Logger.setOracleConnector(oracle);

            Logger.Add(LoggerEventsType.UNKNOWN, "receiver = " + receiver + "\r\ncspProfile = " + cspProfile);

            fasti2.ReceiveMessages(receiver, cspProfile);

            var nonXmlFiles = Array.FindAll(Directory.GetFiles(settings.InXmlDir), x => !x.EndsWith(".xml"));
            foreach (var file in nonXmlFiles)
            {
                string new_file_name = "", eng_file_name = "", new_eng_file_name = "";
                try
                {
                    eng_file_name = new_eng_file_name = oracle.Transliterate(Path.GetFileName(file));
                    if (!File.Exists(settings.WebServerKfmFilesDir + eng_file_name) && !File.Exists(settings.InNonXmlDir + Path.GetFileName(file)))
                    {
                        new_file_name = Path.GetFileName(file);
                        File.Copy(file, settings.WebServerKfmFilesDir + eng_file_name);
                        File.Move(file, settings.InNonXmlDir + Path.GetFileName(file));
                    }
                    else
                    {
                        int j = 1;
                        while (true)
                        {
                            new_file_name = j.ToString() + "_" + Path.GetFileName(file);
                            new_eng_file_name = j.ToString() + "_" + eng_file_name;
                            if (!File.Exists(settings.WebServerKfmFilesDir + new_eng_file_name) && !File.Exists(settings.InNonXmlDir + new_file_name))
                            {
                                File.Copy(file, settings.WebServerKfmFilesDir + new_eng_file_name);
                                File.Move(file, settings.InNonXmlDir + new_file_name);
                                break;
                            }
                            j++;
                        }
                    }
                    oracle.SaveKfmFilename(new_file_name+"/"+new_eng_file_name, File.GetLastWriteTime(settings.InNonXmlDir + new_file_name));
                    Logger.Add(LoggerEventsType.DEBUG, "File " + new_file_name + " successfully moved to NoXmlFilesDir and copied to web-server. Eng Name: "+new_eng_file_name);
                }
                catch (Exception ex)
                {
                    Logger.Add(LoggerEventsType.ERROR, "Try to copy and move file. Initial name: " + file + ". Final name: " + new_file_name + ". Eng Name: " + new_eng_file_name + ". " + ex.ToString());
                }
            }
      
            var files = Directory.GetFiles(settings.InXmlDir, "*.xml");

            XmlDocument document = new XmlDocument();
            foreach (var file in files)
            {
                file_name_err = Path.GetFileName(file);
                document.Load(file);
                var status = SignatureValidateStatus.Valid; // Verify(document);
                string originalid;
                string version;
                string id;
                try
                {
                    originalid = document.DocumentElement["SignedData"]["Data"]["Check"]["OriginalDocumentGuid"].InnerText;
                    version = document.DocumentElement["SignedData"]["Data"]["Check"]["Version"].InnerText;
                    id = document.DocumentElement["SignedData"]["Data"]["Check"]["DocumentUniqueIdentifier"].InnerText;
                }
                catch (Exception ex)
                {
                    Logger.Add(ex);
                    Logger.Add(LoggerEventsType.ERROR, "Не возможно получить доступ к объектам:");

                    try
                    {
                        originalid = document.DocumentElement["SignedData"]["Data"]["Root"]["OriginalDocumentGuid"].InnerText;
                        version = document.DocumentElement["SignedData"]["Data"]["Root"]["Version"].InnerText;
                        id = document.DocumentElement["SignedData"]["Data"]["Root"]["DocumentUniqueIdentifier"].InnerText;
                    }
                    catch (Exception e)
                    {
                        Logger.Add(e);
                        Logger.Add(LoggerEventsType.ERROR, "Не возможно получить доступ к объектам:");
                        Logger.Add(LoggerEventsType.ERROR, "Файл " + file+ " некоректный.");
                        continue;
                    }
                }

                decimal errorcode = -1;
                string error;

                if (status == SignatureValidateStatus.Valid)
                {
                    try
                    {
                        try
                        {
                            errorcode = decimal.Parse(document.DocumentElement["SignedData"]["Data"]["Check"]["ErrorCode"].InnerText);
                            error = document.DocumentElement["SignedData"]["Data"]["Check"]["ErrorName"].InnerText;
                        }
                        catch
                        {
                            errorcode = decimal.Parse(document.DocumentElement["SignedData"]["Data"]["Root"]["ErrorCode"].InnerText);
                            error = document.DocumentElement["SignedData"]["Data"]["Root"]["ErrorName"].InnerText;
                        }
                        oracle.SaveReceiveMessage(id, errorcode, error, document.OuterXml, id, version, originalid);
                    }
                    catch (Exception e)
                    {
                        Logger.Add(e);

                        string description = "";
                        try
                        {
                            description = document.DocumentElement["SignedData"]["Data"]["Root"]["Description"].InnerText;
                            description += " Количество дней на ответ: " + document.DocumentElement["SignedData"]["Data"]["Root"]["CountDays"].InnerText;
                        }
                        finally
                        {
                            oracle.SaveReceiveMessage(id, 0, description, document.OuterXml, id, version, originalid);
                        }
                    }
                }
                else
                {
                    oracle.SaveReceiveMessage(id, -1 * (int)status, Enum.GetName(typeof(SignatureValidateStatus), status), document.OuterXml, id, version, originalid);
                }
                
                try
                {
                    File.Move(file, fasti2.GetArchiveDayFolder(settings.InXmlDirArchive) + Path.GetFileName(file));
                }
                catch (Exception ex)
                {
                    File.Delete(file);
                    Logger.Add(ex);
                }
            }
        }
        catch (Exception e)
        {
            Logger.Add(e);
            if (File.Exists(settings.InXmlErrDir + file_name_err))
            {
                File.Move(settings.InXmlDir + file_name_err, settings.InXmlErrDir + "exists_" + file_name_err);
            }
            else
            {
                File.Move(settings.InXmlDir + file_name_err, settings.InXmlErrDir + file_name_err);
            }
            return e.Message + "\nИмя некорректного файла: " + file_name_err;
        }

        return "";
    }

    private XmlDocument SignDocument(XmlDocument document, string sender, string receiver)
    {
        if ((document != null) && (document.DocumentElement != null))
        {
            XmlNode dataNode = document.DocumentElement;

            Encoding encoding = GetEncoding(document);
            if (document.FirstChild.NodeType != XmlNodeType.XmlDeclaration)
            {
                XmlDeclaration newChild = document.CreateXmlDeclaration("1.0", encoding.HeaderName, null);
                document.InsertBefore(newChild, document.DocumentElement);
            }

            byte[] bytes = encoding.GetBytes(document.OuterXml);

            try
            {
                string sign = Convert.ToBase64String(Sign(bytes));

                return GetSignedDocument(document, dataNode, encoding, sign, sender, receiver);
            }
            catch (Exception exception2)
            {
                Logger.Add(exception2);
                Logger.Add(LoggerEventsType.ERROR, "Ошибка вычисления ЭЦП:\n" + exception2.Message);
                throw new Exception("Ошибка вычисления ЭЦП:\n" + exception2.Message, exception2);
            }
        }
        return null;
    }

    private byte[] Sign(byte[] data)
    {
        byte[] buffer;
        try
        {
            using (MemoryStream stream = new MemoryStream(data))
            {
                XmlDocument document = new XmlDocument();
                document.Load(stream);
                Encoding encoding = GetEncoding(document);
                buffer = new XmlSigned().SignXml(data, settings.CspProfile, "", encoding);
            }
        }
        catch (Exception exception2)
        {
            Logger.Add(exception2);
            throw new Exception(exception2.Message, exception2);
        }
        return buffer;
    }

    private byte[] ProcessingSiganture(string base64Signature, Encoding encoding)
    {
        try
        {
            return Convert.FromBase64String(base64Signature);
        }
        catch (FormatException)
        {
            return encoding.GetBytes(base64Signature);
        }
    }

    private System.Text.Encoding GetEncoding(XmlDocument document)
    {
        if (document.FirstChild.NodeType == XmlNodeType.XmlDeclaration)
        {
            XmlDeclaration firstChild = (XmlDeclaration)document.FirstChild;
            return System.Text.Encoding.GetEncoding(firstChild.Encoding);
        }
        return System.Text.Encoding.GetEncoding("UTF-8");
    }

    private XmlDocument GetSignedDocument(XmlDocument document, XmlNode dataNode, Encoding encoding, string sign, string sender, string receiver)
    {
        XDocument signedDocument = new XDocument(
            new XDeclaration("1.0", encoding.HeaderName, ""),
            new XElement("ExportData",
                new XElement("SignedData",
                    new XElement("Sender", sender),
                    new XElement("Receiver", receiver),
                    new XElement("Data", XElement.Parse(dataNode.OuterXml)),
                    new XElement("TimeStamp", DateTime.Now.ToString("dd.MM.yyyy hh:mm:ss")),
                    new XElement("Signature", sign)
                ),
                new XElement("TransportType", "3")
            )
        );

        return ToXmlDocument(signedDocument);
    }

    private XmlDocument ToXmlDocument(XDocument xDocument)
    {
        var xmlDocument = new XmlDocument();
        using (var xmlReader = xDocument.CreateReader())
        {
            xmlDocument.Load(xmlReader);
            var declare = xmlDocument.CreateXmlDeclaration("1.0", "UTF-8", null);

            //Add the new node to the document.
            XmlElement root = xmlDocument.DocumentElement;
            xmlDocument.InsertBefore(declare, root);
        }
        return xmlDocument;
    }

    private SignatureValidateStatus Verify(XmlDocument convert)
    {
        if (convert.DocumentElement != null)
        {
            XmlNode node = convert.DocumentElement["SignedData"];
            if (node != null)
            {
                XmlNode node2 = node["Signature"];
                if ((node2 != null) && !string.IsNullOrEmpty(node2.InnerText))
                {
                    Encoding encoding = GetEncoding(convert);
                    byte[] signature = ProcessingSiganture(node2.InnerText, encoding);
                    XmlNode node3 = node["Data"];
                    if (node3 != null)
                    {
                        XmlDocument document = new XmlDocument();
                        XmlNode newChild = document.ImportNode(node3.FirstChild, true);
                        document.AppendChild(newChild);
                        XmlDeclaration declaration = document.CreateXmlDeclaration("1.0", encoding.HeaderName, null);
                        document.InsertBefore(declaration, document.DocumentElement);
                        byte[] bytes = encoding.GetBytes(document.InnerXml);

                        return Verify(bytes, signature);
                    }
                }
            }
        }
        return SignatureValidateStatus.NoValidate;
    }

    private SignatureValidateStatus Verify(byte[] data, byte[] signature)
    {
        SignatureValidateStatus noValidate;
        try
        {
            XmlSigned signed = new XmlSigned();
            using (MemoryStream stream = new MemoryStream(data))
            {
                XmlDocument document = new XmlDocument();
                document.Load(stream);

                stream.Position = 0L;
                noValidate = signed.VerifyXml(stream, signature, GetEncoding(document)) ? SignatureValidateStatus.Valid : SignatureValidateStatus.EdsIncorrect;
            }
        }
        catch (Exception exception2)
        {
            throw new Exception(exception2.Message, exception2);
        }
        return noValidate;
    }
}
