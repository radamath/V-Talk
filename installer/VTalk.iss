; V-Talk kurulum betiği (Inno Setup 6). Doğrudan değil, build-installer.ps1 ile derleyin:
;   .\installer\build-installer.ps1
; Sessiz kurulum (GPO / toplu dağıtım):
;   VTalk-Setup.exe /VERYSILENT /ALLUSERS /ADMINHOST=ADMIN-PC /NETWORKS=10.20.30.0/24,10.20.40.0/24

#ifndef SourceDir
  #define SourceDir "..\dist"
#endif
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef OrgPub
  ; Anahtarsız kurulumda istemciler ağda ilk gördükleri dizine güvenir (TOFU); bu yüzden derlenmez.
  #error OrgPub tanımlı değil. Kurulumu build-installer.ps1 ile, organizasyon anahtarıyla derleyin.
#endif
#ifndef OrgFingerprint
  #error OrgFingerprint tanımlı değil. Kurulumu build-installer.ps1 ile derleyin.
#endif
#ifndef DefaultAdminHost
  #define DefaultAdminHost ""
#endif
#ifndef DefaultNetworks
  #define DefaultNetworks ""
#endif

[Setup]
AppId={{F88389BB-4BB2-4D69-9C99-4F074EF700E5}
AppName=V-Talk
AppVersion={#AppVersion}
AppVerName=V-Talk {#AppVersion}
AppPublisher=V-Talk
DefaultDirName={autopf}\V-Talk
DefaultGroupName=V-Talk
DisableProgramGroupPage=yes
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog commandline
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputBaseFilename=VTalk-Setup
SetupIconFile=..\src\VTalk\Assets\vtalk.ico
UninstallDisplayIcon={app}\VTalk.exe
UninstallDisplayName=V-Talk
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
ShowLanguageDialog=no
UsePreviousAppDir=yes

[Languages]
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"

[Tasks]
Name: "desktopicon"; Description: "Masaüstü kısayolu oluştur"; Flags: unchecked

[Files]
Source: "{#SourceDir}\VTalk.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#OrgPub}"; DestDir: "{app}"; DestName: "org.pub"; Flags: ignoreversion
Source: "..\tools\firewall-kurallari.ps1"; DestDir: "{app}\tools"; Flags: ignoreversion; Check: IsAdminInstallMode

[Icons]
Name: "{autoprograms}\V-Talk"; Filename: "{app}\VTalk.exe"; WorkingDir: "{app}"; Comment: "V-Talk LAN Messenger"
Name: "{autodesktop}\V-Talk"; Filename: "{app}\VTalk.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "powershell.exe"; Parameters: "{code:FirewallArgs}"; Flags: runhidden waituntilterminated; StatusMsg: "Güvenlik duvarı kuralları ekleniyor..."; Check: IsAdminInstallMode
Filename: "{app}\VTalk.exe"; Description: "V-Talk'ı başlat"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Get-NetFirewallRule -Group 'V-Talk' -ErrorAction SilentlyContinue | Remove-NetFirewallRule"""; Flags: runhidden waituntilterminated; RunOnceId: "RemoveFirewallRules"

[UninstallDelete]
Type: files; Name: "{app}\deploy.json"

[Code]
var
  OrgPage: TInputQueryWizardPage;
  AdminHost: String;
  Networks: String;

function IsValidHostName(const S: String): Boolean;
var
  I, LabelLen: Integer;
  C: Char;
begin
  Result := False;
  if (Length(S) = 0) or (Length(S) > 253) then Exit;
  LabelLen := 0;
  for I := 1 to Length(S) do
  begin
    C := S[I];
    if C = '.' then
    begin
      if (LabelLen = 0) or (S[I - 1] = '-') then Exit;
      LabelLen := 0;
    end
    else if ((C >= 'a') and (C <= 'z')) or ((C >= 'A') and (C <= 'Z')) or ((C >= '0') and (C <= '9')) or (C = '-') then
    begin
      if (C = '-') and (LabelLen = 0) then Exit;
      LabelLen := LabelLen + 1;
      if LabelLen > 63 then Exit;
    end
    else
      Exit;
  end;
  Result := (LabelLen > 0) and (S[Length(S)] <> '-');
end;

function IsValidIPv4Cidr(E: String): Boolean;
var
  P, N, Octets, V: Integer;
  Part, Ip: String;
begin
  Result := False;
  P := Pos('/', E);
  if P > 0 then
  begin
    Part := Copy(E, P + 1, Length(E));
    if (Length(Part) = 0) or (Length(Part) > 2) then Exit;
    for N := 1 to Length(Part) do
      if (Part[N] < '0') or (Part[N] > '9') then Exit;
    V := StrToIntDef(Part, -1);
    if (V < 1) or (V > 32) then Exit;
    Ip := Copy(E, 1, P - 1);
  end
  else
    Ip := E;
  Octets := 0;
  Ip := Ip + '.';
  while Length(Ip) > 0 do
  begin
    P := Pos('.', Ip);
    Part := Copy(Ip, 1, P - 1);
    if (Length(Part) = 0) or (Length(Part) > 3) then Exit;
    for N := 1 to Length(Part) do
      if (Part[N] < '0') or (Part[N] > '9') then Exit;
    V := StrToIntDef(Part, -1);
    if (V < 0) or (V > 255) then Exit;
    Octets := Octets + 1;
    Delete(Ip, 1, P);
  end;
  Result := Octets = 4;
end;

{ Ağ listesini doğrular ve virgülle ayrılmış biçime getirir. }
function NormalizeNetworks(S: String; var Normalized: String): Boolean;
var
  P: Integer;
  E: String;
begin
  Result := False;
  Normalized := '';
  if Length(S) > 2000 then Exit;
  StringChangeEx(S, ';', ',', True);
  StringChangeEx(S, ' ', ',', True);
  StringChangeEx(S, #9, ',', True);
  S := S + ',';
  while Length(S) > 0 do
  begin
    P := Pos(',', S);
    E := Trim(Copy(S, 1, P - 1));
    Delete(S, 1, P);
    if E <> '' then
    begin
      if not IsValidIPv4Cidr(E) then Exit;
      if Normalized <> '' then Normalized := Normalized + ',';
      Normalized := Normalized + E;
    end;
  end;
  Result := True;
end;

function ValidateOrgInputs(var Err: String): Boolean;
var
  Normalized: String;
begin
  Result := False;
  AdminHost := Trim(OrgPage.Values[0]);
  if not IsValidHostName(AdminHost) then
  begin
    Err := 'Geçerli bir admin bilgisayar adı girin (harf, rakam, tire ve nokta).';
    Exit;
  end;
  if not NormalizeNetworks(OrgPage.Values[1], Normalized) then
  begin
    Err := 'Uzak ağlar geçersiz. Örnek: 10.20.30.0/24, 10.20.40.15';
    Exit;
  end;
  Networks := Normalized;
  Result := True;
end;

procedure InitializeWizard;
var
  V: String;
begin
  OrgPage := CreateInputQueryPage(wpSelectDir, 'Organizasyon', 'Admin bilgisayarı ve uzak ağlar',
    'Yetki ve birim değişiklikleri yalnızca bu kuruluma gömülü organizasyon anahtarıyla imzalanmışsa kabul edilir.' + #13#10 +
    'Admin bilgisayar adı, VPN veya farklı alt ağlardaki bilgisayarların admini bulması için kullanılır.' + #13#10#13#10 +
    'Organizasyon anahtarı parmak izi: {#OrgFingerprint}');
  OrgPage.Add('Admin bilgisayarının adı:', False);
  OrgPage.Add('Uzak ağlar (isteğe bağlı; VPN / şube), ör. 10.20.30.0/24, 10.20.40.0/24:', False);

  V := ExpandConstant('{param:ADMINHOST|}');
  if V = '' then V := GetPreviousData('AdminHost', '');
  if V = '' then V := '{#DefaultAdminHost}';
  OrgPage.Values[0] := V;
  V := ExpandConstant('{param:NETWORKS|}');
  if V = '' then V := GetPreviousData('Networks', '');
  if V = '' then V := '{#DefaultNetworks}';
  OrgPage.Values[1] := V;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Err: String;
begin
  Result := True;
  if CurPageID = OrgPage.ID then
  begin
    Result := ValidateOrgInputs(Err);
    if not Result then MsgBox(Err, mbError, MB_OK);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Err: String;
begin
  Result := '';
  if not ValidateOrgInputs(Err) then Result := Err;
end;

procedure RegisterPreviousData(PreviousDataKey: Integer);
begin
  SetPreviousData(PreviousDataKey, 'AdminHost', AdminHost);
  SetPreviousData(PreviousDataKey, 'Networks', Networks);
end;

function FirewallArgs(Param: String): String;
begin
  Result := '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\tools\firewall-kurallari.ps1') +
            '" -ExePath "' + ExpandConstant('{app}\VTalk.exe') + '"';
  if Networks <> '' then Result := Result + ' -RemoteSubnets ' + Networks;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Json: String;
begin
  if CurStep = ssPostInstall then
  begin
    { Değerler yukarıda katı biçimde doğrulandı; JSON kaçış gerektiren karakter içermez. }
    Json := '{' + #13#10 +
            '  "adminHost": "' + AdminHost + '",' + #13#10 +
            '  "networks": "' + Networks + '"' + #13#10 +
            '}' + #13#10;
    if not SaveStringToFile(ExpandConstant('{app}\deploy.json'), Json, False) then
      MsgBox('Kurulum ayarları yazılamadı: ' + ExpandConstant('{app}\deploy.json'), mbError, MB_OK);
  end;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo,
  MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := MemoDirInfo + NewLine + NewLine +
            'Organizasyon:' + NewLine +
            Space + 'Anahtar parmak izi: {#OrgFingerprint}' + NewLine +
            Space + 'Admin bilgisayarı: ' + AdminHost + NewLine;
  if Networks <> '' then Result := Result + Space + 'Uzak ağlar: ' + Networks + NewLine;
  Result := Result + NewLine;
  if IsAdminInstallMode then
    Result := Result + 'Güvenlik duvarı kuralları otomatik eklenecek.'
  else
    Result := Result + 'Yalnızca bu kullanıcıya kurulum: güvenlik duvarı kuralı eklenemez, ilk açılışta Windows izin isteyebilir.';
  if MemoTasksInfo <> '' then Result := Result + NewLine + NewLine + MemoTasksInfo;
end;
