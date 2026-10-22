using System.Buffers.Binary;
using System.IO;
using System.Printing;
using System.Text;
using System.Text.Json;
using Isafom.ConfigExtractor;

internal static class ConfigurationTests
{
    public static void Run(Action<bool,string> assert,string secret,string personal)
    {
        // Both identity/credential strings are created by the caller only in
        // memory. No raw synthetic or real ticket is written to a fixture file.
        const string framework="http://schemas.microsoft.com/windows/2003/08/printing/printschemaframework";
        const string vendor="http://schemas.kyocera.com/printers/KxDriver/v_1_0_";
        string Ticket(string account,string code,string ns=vendor)=>$"""
          <f:PrintTicket xmlns:f="{framework}" xmlns:k="{ns}" xmlns:p="https://untrusted.invalid/{personal}">
           <f:Feature name="k:JobAccounting"><f:Option name="k:UseSpecificID"/></f:Feature>
           <f:Feature name="k:JobStorage"><f:Option name="k:PrivatePrint"/></f:Feature>
           <f:ParameterInit name="k:JobAccountID"><f:Value>{account}</f:Value></f:ParameterInit>
           <f:ParameterInit name="k:JobStorageAccessCode"><f:Value>{code}</f:Value></f:ParameterInit>
           <f:ParameterInit name="k:UserName"><f:Value>{personal}</f:Value></f:ParameterInit>
           <f:ParameterInit name="k:PageDevmodeSnapshot"><f:Value>{secret}</f:Value></f:ParameterInit>
          </f:PrintTicket>
          """;
        PersonalReadback Read(string xml){using var stream=new MemoryStream(Encoding.UTF8.GetBytes(xml));return PersonalReadbackReader.Read(stream,"Test");}
        var same=Read(Ticket(secret,secret));
        assert(same.AccountingMode=="UseSpecificID"&&same.StorageMode=="PrivatePrint","Configuration: exact vendor namespace modes decoded");
        assert(same.AccountAndPrivateCodeEqual==true&&same.BothCodesAsciiDigits==true,"Configuration: exact code comparison occurs in memory");
        string safe=JsonSerializer.Serialize(same);
        assert(!safe.Contains(secret)&&!safe.Contains(personal)&&!safe.Contains("PageDevmodeSnapshot"),"Configuration: personal values and private snapshots excluded from DTO");
        assert(Read(Ticket(secret,"1"+secret)).AccountAndPrivateCodeEqual==false,"Configuration: leading zero is not normalized away");
        var empty=Read(Ticket(secret,""));
        assert(empty.PrivateCodeValueState=="Presente ma vuoto"&&empty.AccountAndPrivateCodeEqual is null,"Configuration: empty private field is not a false comparison");
        var spoof=Read(Ticket(secret,secret,"https://untrusted.invalid"));
        assert(spoof.AccountingMode=="Non esposto"&&spoof.AccountValueState=="Non esposto","Configuration: lookalike vendor QName rejected");
        string standardNone=Ticket(secret,secret).Replace($"xmlns:p=\"https://untrusted.invalid/{personal}\"",$"xmlns:p=\"http://schemas.microsoft.com/windows/2003/08/printing/printschemakeywords\"").Replace("name=\"k:PrivatePrint\"","name=\"p:None\"");
        assert(Read(standardNone).StorageMode=="None","Configuration: exact standard None is supported under KX JobStorage");
        var duplicate=Read(Ticket(secret,secret).Replace("</f:PrintTicket>","<f:ParameterInit name=\"k:JobAccountID\"><f:Value>On</f:Value></f:ParameterInit></f:PrintTicket>"));
        assert(duplicate.AccountValueState=="Ambiguo"&&duplicate.AccountAndPrivateCodeEqual is null,"Configuration: duplicate credential parameters cannot certify equality");
        var nested=Read(Ticket(secret,secret).Replace($"<f:Value>{secret}</f:Value>",$"<f:Value><f:Value>{secret}</f:Value></f:Value>"));
        assert(nested.AccountValueState=="Valore non semplice","Configuration: nested secret values rejected");
        bool blocked=false;try{Read("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///C:/Windows/win.ini'>]><x>&e;</x>");}catch(System.Xml.XmlException){blocked=true;}
        assert(blocked,"Configuration: external entities prohibited");
        string capabilities=$"""
          <f:PrintCapabilities xmlns:f="{framework}" xmlns:k="{vendor}">
           <f:ParameterDef name="k:JobAccountID"><f:Property name="f:MaxLength"><f:Value>8</f:Value></f:Property><f:Property name="f:DefaultValue"><f:Value>{secret}</f:Value></f:Property></f:ParameterDef>
           <f:ParameterDef name="k:JobStorageAccessCode"><f:Property name="f:MinLength"><f:Value>4</f:Value></f:Property></f:ParameterDef>
          </f:PrintCapabilities>
          """;
        using(var stream=new MemoryStream(Encoding.UTF8.GetBytes(capabilities)))
        {
            var rules=PersonalReadbackReader.ReadRules(stream);
            assert(rules.Length==2&&!JsonSerializer.Serialize(rules).Contains(secret),"Configuration: only structural constraints exported, never personal defaults");
        }
        var ticket=new PrintTicket {OutputColor=OutputColor.Monochrome,CopyCount=2,PageMediaSize=new PageMediaSize(PageMediaSizeName.ISOA4)};
        var fields=StandardSettings.ReadProperties(ticket,typeof(PrintTicket),"Test");
        assert(fields.Single(f=>f.Field=="OutputColor").Value?.GetString()=="Monochrome","Configuration: standard enum reflected with its documented name");
        assert(fields.Single(f=>f.Field=="CopyCount").Value?.GetInt32()==2,"Configuration: numeric standard setting preserved");
        assert(fields.Single(f=>f.Field=="Duplexing").State=="Non esposto","Configuration: unspecified standard property stays unknown");
        assert(fields.Single(f=>f.Field=="PageMediaSize").Unit.Contains("1/96"),"Configuration: media dimensions carry DIP units");
        assert(StandardSettings.Normalize((OutputColor)int.MaxValue).Complete==false,"Configuration: unknown enum cannot escape as arbitrary integer");
        assert(StandardSettings.Normalize(secret).Safe is null,"Configuration: arbitrary strings cannot pass standard serializer");
        assert(StandardSettings.Normalize(double.PositiveInfinity).Safe is null,"Configuration: nonfinite dimensions excluded");
        var dm=new byte[220];BinaryPrimitives.WriteUInt16LittleEndian(dm.AsSpan(68),220);
        BinaryPrimitives.WriteUInt32LittleEndian(dm.AsSpan(72),0x800|0x1000);
        BinaryPrimitives.WriteInt16LittleEndian(dm.AsSpan(92),1);BinaryPrimitives.WriteInt16LittleEndian(dm.AsSpan(94),2);
        Encoding.Unicode.GetBytes(personal).AsSpan(0,Math.Min(Encoding.Unicode.GetByteCount(personal),64)).CopyTo(dm);
        var native=PublicDevMode.Parse(dm,"Test");
        assert(native.Single(f=>f.Field=="Color").Value?.GetProperty("Symbol").GetString()=="Monochrome","Configuration: documented DEVMODE color decoded");
        assert(native.Single(f=>f.Field=="Duplex").Value?.GetProperty("Symbol").GetString()=="TwoSidedLongEdge","Configuration: documented DEVMODE duplex decoded");
        assert(native.Single(f=>f.Field=="Copies").Value is null,"Configuration: unset DEVMODE field never reported as zero");
        assert(!JsonSerializer.Serialize(native).Contains(personal),"Configuration: device and form name bytes omitted");
        assert(PublicDevMode.Parse(dm.AsSpan(0,100),"Test")[0].State=="Non valida o incompleta","Configuration: truncated public DEVMODE rejected");
        BinaryPrimitives.WriteUInt16LittleEndian(dm.AsSpan(68),94);
        assert(PublicDevMode.Parse(dm,"Test").Single(f=>f.Field=="Duplex").Value is null,"Configuration: field bounds follow dmSize rather than array size");
        using var initial=SentinelSnapshot.Fixture(("K01","UserName",1u,Encoding.Unicode.GetBytes("original")));
        using var modified=SentinelSnapshot.Fixture(("K01","UserName",1u,Encoding.Unicode.GetBytes(personal+"\0")));
        using var restored=SentinelSnapshot.Fixture(("K01","UserName",1u,Encoding.Unicode.GetBytes("original")));
        var diff=initial.Compare(modified,personal);
        assert(diff.Length==1&&diff[0].ContainsUtf16Pattern&&diff[0].ExactStringMatch,"Sentinel: exact UTF-16 registry match detected without offset guessing");
        assert(!JsonSerializer.Serialize(diff).Contains(personal)&&!JsonSerializer.Serialize(diff).Contains("original"),"Sentinel: neither original nor temporary identity exported");
        assert(initial.Restored(restored)==true&&initial.Restored(modified)==false,"Sentinel: restoration compares actual original bytes");
        assert(!initial.Contains(personal)&&modified.Contains(personal),"Sentinel: baseline collision and post-change pattern scans distinguish observations");
        using var binary=SentinelSnapshot.Fixture(("K01",personal,3u,Encoding.UTF8.GetBytes(secret)));
        var binaryDiff=initial.Compare(binary,secret);
        assert(binaryDiff[0].ContainsUtf8Pattern&&!binaryDiff[0].ExactStringMatch,"Sentinel: binary pattern is not claimed as an exact string mapping");
        assert(!JsonSerializer.Serialize(binaryDiff).Contains(personal)&&!JsonSerializer.Serialize(binaryDiff).Contains(secret),"Sentinel: unknown registry names and secret payloads omitted");
        using var created=SentinelSnapshot.Fixture(("K02","Settings",4u,BitConverter.GetBytes(1)));
        var structure=initial.Compare(created,personal);
        assert(structure.Any(d=>d.Change=="Comparso")&&structure.Any(d=>d.Change=="Rimosso"),"Sentinel: added and removed values both invalidate restoration");
        using var originalWithCounter=SentinelSnapshot.FixtureCells(
            ("counter",new("Macchina / coda","ChangeID",4u,BitConverter.GetBytes(1),true)),
            ("settings",new("Macchina / coda","Settings",3u,Encoding.UTF8.GetBytes("original"))));
        using var restoredWithNewCounter=SentinelSnapshot.FixtureCells(
            ("counter",new("Macchina / coda","ChangeID",4u,BitConverter.GetBytes(2),true)),
            ("settings",new("Macchina / coda","Settings",3u,Encoding.UTF8.GetBytes("original"))));
        var counterAssessment=originalWithCounter.AssessRestored(restoredWithNewCounter);
        assert(counterAssessment.AllReadableBytesRestored==false&&counterAssessment.ConfigurationExcludingChangeIdRestored==true,"Sentinel: documented ChangeID update is separated from configuration restoration");
        assert(counterAssessment.WindowsChangeIdentifierDifferences.Length==1&&counterAssessment.RemainingConfigurationDifferences.Length==0,"Sentinel: excluded change identifier remains visible in evidence");
        using var wrongType=SentinelSnapshot.FixtureCells(("counter",new("Macchina / coda","ChangeID",3u,BitConverter.GetBytes(2),true)),("settings",new("Macchina / coda","Settings",3u,Encoding.UTF8.GetBytes("original"))));
        assert(originalWithCounter.AssessRestored(wrongType).ConfigurationExcludingChangeIdRestored==false,"Sentinel: a ChangeID type change is never ignored");
        using var vendorCounter=SentinelSnapshot.FixtureCells(("counter",new("Vendor / settings","ChangeID",4u,BitConverter.GetBytes(2),false)),("settings",new("Macchina / coda","Settings",3u,Encoding.UTF8.GetBytes("original"))));
        assert(originalWithCounter.AssessRestored(vendorCounter).ConfigurationExcludingChangeIdRestored==false,"Sentinel: vendor lookalike identifiers are never ignored");
        using var changedSettings=SentinelSnapshot.FixtureCells(("counter",new("Macchina / coda","ChangeID",4u,BitConverter.GetBytes(2),true)),("settings",new("Macchina / coda","Settings",3u,Encoding.UTF8.GetBytes(personal))));
        assert(originalWithCounter.AssessRestored(changedSettings).ConfigurationExcludingChangeIdRestored==false,"Sentinel: changed actual configuration still blocks restoration");
        using var missingCounter=SentinelSnapshot.FixtureCells(("settings",new("Macchina / coda","Settings",3u,Encoding.UTF8.GetBytes("original"))));
        assert(originalWithCounter.AssessRestored(missingCounter).ConfigurationExcludingChangeIdRestored==false,"Sentinel: missing change-identifier data is not silently excluded");
        string shortCode=secret[..4];
        string markerKey="LocalMachine\0SYSTEM\\CurrentControlSet\\Control\\Print\\Printers\\"+personal+"\0\\PrinterDriverData\0"+personal;
        using var markerInitial=SentinelSnapshot.Fixture((markerKey,personal,3u,Encoding.UTF8.GetBytes("unchanged")));
        using var markerChanged=SentinelSnapshot.Fixture((markerKey,personal,3u,Encoding.UTF8.GetBytes(personal+"/"+shortCode)));
        var markerHits=markerInitial.FindMarkers(markerChanged,personal,shortCode);
        assert(markerHits.Length==1&&markerHits[0].After.NameUtf8&&markerHits[0].After.CodeUtf8,"Markers: exact name and four-character code found in binary payload");
        assert(!markerHits[0].Before.Any&&markerHits[0].ContentChanged,"Markers: newly introduced matches distinguished from baseline collisions");
        assert(markerHits[0].Location.EndsWith("<selected queue>\\PrinterDriverData"),"Markers: physical queue subkey retained without raw queue name");
        string markerJson=JsonSerializer.Serialize(markerHits);
        assert(!markerJson.Contains(personal)&&!markerJson.Contains(shortCode)&&!markerJson.Contains(secret),"Markers: names, codes, raw value identifiers and payloads absent from evidence");
        var existingHits=markerChanged.FindMarkers(markerChanged,personal,shortCode);
        assert(existingHits[0].Before.Any&&!existingHits[0].ContentChanged,"Markers: preexisting literal match cannot prove a GUI change");
        using var markerUnicode=SentinelSnapshot.Fixture(("Native:9","DEVMODE",3u,Encoding.Unicode.GetBytes(personal+"/"+shortCode)));
        var unicodeHits=markerInitial.FindMarkers(markerUnicode,personal,shortCode);
        assert(unicodeHits.Single(h=>h.After.Any).After.NameUtf16&&unicodeHits.Single(h=>h.After.Any).After.CodeUtf16,"Markers: UTF-16 matches found separately");
        assert(unicodeHits.Single(h=>h.After.Any).Location=="GetPrinter level 9 / current-user DEVMODE","Markers: native scope distinguished from registry storage");
        using var markerDword=SentinelSnapshot.Fixture(("K01","AccountID",4u,BitConverter.GetBytes(uint.Parse(shortCode,System.Globalization.CultureInfo.InvariantCulture))));
        assert(markerInitial.FindMarkers(markerDword,personal,shortCode).Single(h=>h.After.Any).After.CodeExactDword,"Markers: exact REG_DWORD numeric candidate identified without returning its value");
        using var binaryDword=SentinelSnapshot.Fixture(("K01","AccountID",3u,BitConverter.GetBytes(uint.Parse(shortCode,System.Globalization.CultureInfo.InvariantCulture))));
        assert(!markerInitial.FindMarkers(binaryDword,personal,shortCode).Any(h=>h.After.CodeExactDword),"Markers: arbitrary binary integer is not classified as REG_DWORD");
        assert(markerInitial.FindMarkers(markerInitial,personal,shortCode).Length==0,"Markers: absent literals remain absent without guessed offsets");
        RequestedCodeReadback Compare(string xml,string expected){using var stream=new MemoryStream(Encoding.UTF8.GetBytes(xml));return PersonalReadbackReader.CompareRequestedCode(stream,"Test",expected);}
        var requested=Compare(Ticket(shortCode,shortCode),shortCode);
        assert(requested.AccountMatchesRequested==true&&requested.PrivateMatchesRequested==true&&requested.BothLiteralValuesMatch,"RequestedCode: both exact literals match the requested string");
        var partial=Compare(Ticket(shortCode,""),shortCode);
        assert(partial.AccountMatchesRequested==true&&partial.PrivateMatchesRequested is null&&!partial.BothLiteralValuesMatch,"RequestedCode: empty private parameter cannot certify private password");
        string noLeadingZero=uint.Parse(shortCode,System.Globalization.CultureInfo.InvariantCulture).ToString(System.Globalization.CultureInfo.InvariantCulture);
        assert(Compare(Ticket(noLeadingZero,shortCode),shortCode).AccountMatchesRequested==false,"RequestedCode: leading zeros are compared exactly");
        assert(Compare(Ticket(shortCode,shortCode,"https://untrusted.invalid"),shortCode).AccountMatchesRequested is null,"RequestedCode: wrong namespace cannot certify a credential");
        var duplicateRequested=Ticket(shortCode,shortCode).Replace("</f:PrintTicket>",$"<f:ParameterInit name=\"k:JobAccountID\"><f:Value>{shortCode}</f:Value></f:ParameterInit></f:PrintTicket>");
        assert(Compare(duplicateRequested,shortCode).AccountMatchesRequested is null,"RequestedCode: duplicate account stays unverifiable even when equal");
        string comparisonJson=JsonSerializer.Serialize(requested);
        assert(!comparisonJson.Contains(shortCode)&&!comparisonJson.Contains(personal),"RequestedCode: comparison DTO never exports input or ticket identity");
        bool invalidCode=false;try{Compare(Ticket(shortCode,shortCode),"A"+shortCode[1..]);}catch(ArgumentException){invalidCode=true;}
        assert(invalidCode,"RequestedCode: non-ASCII-digit requested codes rejected");
        bool nullCode=false;try{Compare(Ticket(shortCode,shortCode),null!);}catch(ArgumentException){nullCode=true;}
        assert(nullCode,"RequestedCode: null input is rejected before XML parsing");
        string builderCaps=$"""
          <f:PrintCapabilities xmlns:f="{framework}" xmlns:k="{vendor}" xmlns:p="http://schemas.microsoft.com/windows/2003/08/printing/printschemakeywords">
           <f:Feature name="k:JobAccounting"><f:Option name="k:UseSpecificID"><f:Property name="f:DisplayName"><f:Value>{personal}</f:Value></f:Property><f:ScoredProperty name="k:JobAccountID"><f:ParameterRef name="k:JobAccountID"/></f:ScoredProperty></f:Option></f:Feature>
           <f:Feature name="k:JobStorage"><f:Option name="k:PrivatePrint"><f:Property name="f:DisplayName"><f:Value>{personal}</f:Value></f:Property></f:Option></f:Feature>
           <f:Feature name="p:PageOutputColor"><f:Option name="p:Monochrome"/></f:Feature>
           <f:ParameterDef name="k:JobAccountID"/><f:ParameterDef name="k:JobStorageAccessCode"/><f:ParameterDef name="k:JobStorageUseAccessCode"/>
          </f:PrintCapabilities>
          """;
        var builderDocument=new System.Xml.XmlDocument{XmlResolver=null};builderDocument.LoadXml(builderCaps);
        var constructed=DirectConversionProbe.NewTicket();DirectConversionProbe.ApplyVendorRequest(constructed,builderDocument,shortCode);
        assert(!constructed.OuterXml.Contains(personal)&&constructed.GetElementsByTagName("Property",framework).Count==0,"DirectConversion: capability display metadata is not copied into a ticket");
        assert(constructed.GetElementsByTagName("ScoredProperty",framework).Count==1,"DirectConversion: required scored parameter reference retained");
        assert(constructed.GetElementsByTagName("ParameterDef",framework).Count==0,"DirectConversion: capability definitions are not emitted as ticket initializers");
        var constructedRead=Compare(constructed.OuterXml,shortCode);
        assert(constructedRead.BothLiteralValuesMatch,"DirectConversion: in-memory requested code remains exact before provider conversion");
        DirectConversionProbe.ApplyVendorRequest(constructed,builderDocument,shortCode);
        assert(constructed.GetElementsByTagName("ParameterInit",framework).Count==3&&constructed.GetElementsByTagName("Feature",framework).Count==3,"DirectConversion: applying the request twice does not duplicate credentials or features");
        var requestJson=JsonSerializer.Serialize(new{UserName=" "+personal+" ",AccountId=shortCode});
        var input=IdentityVerification.ReadRequest(new StringReader(requestJson));
        assert(input.UserName==" "+personal+" "&&input.AccountId==shortCode,"IdentityProtocol: request preserves exact name spaces and leading zeros");
        string unicodeName="TEST_\u00e9\u03a9_"+personal;
        byte[] unicodeRequest=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{UserName=unicodeName,AccountId=shortCode}));
        using(var utf8Stream=new MemoryStream(unicodeRequest))
        using(var utf8Reader=new StreamReader(utf8Stream,new UTF8Encoding(false,true)))
            assert(IdentityVerification.ReadRequest(utf8Reader).UserName==unicodeName,"IdentityProtocol: UTF-8 names remain exact without normalization");
        bool duplicateInput=false;try{IdentityVerification.ReadRequest(new StringReader(requestJson[..^1]+",\"AccountId\":\""+shortCode+"\"}"));}catch(ArgumentException){duplicateInput=true;}
        assert(duplicateInput,"IdentityProtocol: duplicate credential fields rejected");
        bool extraInput=false;try{IdentityVerification.ReadRequest(new StringReader(requestJson[..^1]+",\"Extra\":true}"));}catch(ArgumentException){extraInput=true;}
        assert(extraInput,"IdentityProtocol: unrelated input fields rejected");
        bool oversizedInput=false;try{IdentityVerification.ReadRequest(new StringReader(new string(' ',2049)));}catch(ArgumentException){oversizedInput=true;}
        assert(oversizedInput,"IdentityProtocol: input bounded before unbounded line allocation");
        var reportOnly=new IdentityVerificationResult(DossierWriter.Version,true,true,null,[requested],[same],true,null,
            counterAssessment,false,false,"Test");
        string reportOnlyJson=JsonSerializer.Serialize(reportOnly);
        assert(!reportOnlyJson.Contains(personal)&&!reportOnlyJson.Contains(shortCode)&&!reportOnlyJson.Contains(secret),"IdentityProtocol: result cannot serialize request identity or code");
        assert(!reportOnly.AllPersonalFieldsVerified&&!reportOnly.ProductionWriteMethodVerified&&reportOnly.ExactGuiUserNameMatches is null,"IdentityProtocol: literal name evidence cannot unlock installation");
        string description=IdentityVerifierWindow.Describe(reportOnly);
        assert(description.Contains("parziale")&&!description.Contains(personal)&&!description.Contains(shortCode),"IdentityUI: partial results are explicit and exclude entered values");
        var verifier=new IdentityVerifierWindow(new Isafom.Collector.QueueChoice(personal,"Test driver","Test port",false,"Test"));
        assert(verifier.FindName("AccountCodeInput") is System.Windows.Controls.PasswordBox,"IdentityUI: code entry uses a masked password control");
        assert(verifier.FindName("VerifyButton") is System.Windows.Controls.Button {IsEnabled:false},"IdentityUI: empty fields cannot start a verification");
    }
}
