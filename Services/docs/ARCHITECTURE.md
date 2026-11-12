# SolvencyProof – Architektúra és fájlleírás

Ez a dokumentum fájlonként leírja, hogy a repóban az egyes fájlok mit csinálnak, és miért felelnek.

## 1. Áttekintés

A projekt egy **fizetőképességi bizonyíték (proof of solvency)** demó. Három összetevőből áll:

| Összetevő | Mappa | Technológia | Szerep |
|---|---|---|---|
| Webalkalmazás | `web/` | ASP.NET Core Blazor Server, EF Core, SQL Server | UI, adatbázis, az audit vezénylése (orkesztrátor) |
| Prover | `calc/` | Rust, Axum, Plonky3 | Valódi zk-STARK bizonyíték (AIR + hiding FRI) előállítása és ellenőrzése, `127.0.0.1:3000` |
| Okosszerződések | `SolvencyProofContract/` | Hardhat, Solidity, TypeScript | On-chain ellenőrzés, `127.0.0.1:8545` |

### Az audit folyamata

```
Blazor UI / API
      │  GenerateProofAsync(auditId)
      ▼
AuditProverService (web)
  1. Egyenlegek beolvasása (SQL Server) ─► Poseidon2 Merkle-összegfa (MerkleTreeBuilder)
  2. Eszköz-elkötelezettség C_A = Poseidon2(a, r), r: 256 bites véletlen vakító tényező
  3. ZkProofClient ──HTTP POST──► Rust prover (/api/v1/generate-proof)
        a tanú (levelek, eszközök, r) csak loopbacken utazik
        a Rust újraszámolja a gyökeret és C_A-t, egyezést vár (különben 409),
        bizonyít, és a bizonyítást saját maga ellenőrzi, mielőtt visszaadja
        ◄── proof_elements, public_inputs, proof, depth, verified ──
  4. HardhatProofSubmitterService ──JSON-RPC──► ProofOfSolvency.submitAndVerifyProof
        └─► MockStarkVerifier.verifyProof (álverifikátor)
  5. Receipt megvárása; a nyugtában ott kell lennie a SolvencyVerified eseménynek
     → AuditStatus.VerifiedOnChain
```

### Mit bizonyít a zk-STARK, és mi nyilvános

A bizonyítás állítása: *a nyilvános Merkle-gyökérhez tartozó fa minden egyenlege 0 és 2^40 közé esik, az egyenlegek összege `T`, az eszközök `a`, és `a − T ∈ [0, 2^62)`, vagyis `a ≥ T`*. Az `a` és a `T` is titkos.

| | |
|---|---|
| **Nyilvános** | a kötelezettségfa gyökere, az eszköz-elkötelezettség `C_A`, az auditra kötő nonce, a fa mélysége (így a számlák számának nagyságrendje) |
| **Titkos** | az egyenlegek, az összegük `T`, az eszközök `a`, a vakító tényező `r` |

A nullaismeret a *verifier* felé érvényes. A prover az intézmény saját, megbízható környezetében fut, ezért kapja meg a tanút. A hiding FRI minden bizonyításhoz friss, az operációs rendszertől származó véletlent használ.

Az audit állapotai (`AuditStatus`): `Prepared → Proving → VerifiedOnChain`, hiba esetén `Failed`.

A lépések sorszámozása megegyezik az `AuditProgressReport.StepIndex` értékeivel (1–4 = az éppen elindult lépés, 5 = minden lépés kész).

---

## 2. `web/` – ASP.NET Core alkalmazás

### 2.1 `web/Services/` – üzleti logika

| Fájl | Felelősség |
|---|---|
| `AuditProverService.cs` | **Az audit-pipeline vezénylője.** A `GenerateProofAsync(auditId, progress, ct)` a belépési pont. Saját DI scope-ot nyit (`IServiceScopeFactory`), mert a hívó scope-ja a kérés végén megszűnik. Hiba esetén `Failed` státuszt állít és naplóz. A `RunPipelineAsync` végzi a négy lépést (lásd fent), elmenti az elköteleződéseket, a bizonyítékot és a Data Protectionnel titkosított vakító faktort, és az `IProgress<AuditProgressReport>`-on keresztül jelent a UI-nak. |
| `IAuditProverService.cs` | Az előbbi szolgáltatás interfésze. A UI és az endpointok ezen keresztül hívják, így cserélhető és tesztelhető. |
| `MerkleTreeBuilder.cs` | **A kötelezettségek Poseidon2 Merkle-összegfája.** Levél: `Poseidon2(id, egyenleg, só)`, csomópont: `Poseidon2(bal, jobb, bal-összeg, jobb-összeg)`, így a gyökér a teljes összeget is köti. A fának legalább 2^6 helye van (az üres helyek nulla-levelek), a mélység a számlák számától függ. Az egyenleg centben, `[0, 2^40)` tartományban kell legyen. Itt készül az ügyfél beillesztési bizonyítéka (`BuildInclusionProof`) és annak ellenőrzése. A Rust `calc/src/hash.rs` ugyanezt valósítja meg, a kettőnek bájtra azonos eredményt kell adnia. |
| `AccountInclusionVerifier.cs` | **Az ügyfél-hitelesítés logikája.** Az utolsó `VerifiedOnChain` auditot veszi, a gyökeret a lánc `SolvencyVerified` eseményéből olvassa (nem az adatbázisból), az ügyfél útvonalából újraszámolt gyökeret ehhez hasonlítja, és a Rust `verify-proof` végponttal ellenőrzi az audit zk-STARK bizonyítékát. |
| `ZkProofClient.cs` | **HTTP kliens a Rust prover felé.** `GenerateProofAsync` (`api/v1/generate-proof`: a tanú és az elvárt gyökér/elkötelezettség) és `VerifyProofAsync` (`api/v1/verify-proof`: csak nyilvános adat). A cím alapból `http://127.0.0.1:3000/`, a `ZkProver:BaseUrl` konfigurációval felülírható. |
| `Blockchain/HardhatProofSubmitterService.cs` | **Nethereum alapú blokklánc-kliens** (singleton). A konstruktor a `Hardhat` konfigszekciót (`RpcUrl`, `PrivateKey`, `ContractAddress`) validálja, és betölti az ABI-t (`SolvencyContractAbi.json`). A `SendProofAsync` elküldi a `submitAndVerifyProof` tranzakciót, a `WaitForVerificationAsync` megvárja a receiptet (status ≠ 1 esetén hiba), a `ParseUint256` pedig a 0x-előtagú hex stringeket `uint256`-tá alakítja. |
| `SolvencyProofContext.cs` | **EF Core `DbContext`.** `DbSet`-ek: `Account`, `SolvencyAudit`, `CryptographicSecrets`, `VerificationQueries`. Az `OnModelCreating` beállítja az Audit↔Secret 1:1 kapcsolatot, és három demó számlát seedel. |

### 2.2 `web/Helpers/` – kriptográfiai építőkockák

| Fájl | Felelősség |
|---|---|
| `Poseidon2Goldilocks.cs`, `Poseidon2GoldilocksConstants.g.cs` | **A Rust prover hash-ének portja** (Poseidon2, Goldilocks, 12 széles). A konstansokat a Rust forrásból generálja a `write_csharp_constants` teszt, kézzel nem szerkesztendők. Itt van az eszköz-elkötelezettség (`CommitAssets`) és a digest/testelem kódolás is. |
| `CryptoHelper.cs` | Kriptográfiailag biztonságos véletlen bájtok (`RandomNumberGenerator`), ebből készül a vakító faktor. |

### 2.3 `web/Models/` – adatmodellek és DTO-k

| Fájl | Felelősség |
|---|---|
| `Account.cs` | Ügyfélszámla: `Id`, `Balance`, `MerkleLeafSalt`, `LastUpdate`. Ebből lesz a Merkle-levél. |
| `SolvencyAudit.cs` | Egy audit rekordja: időbélyeg, `TotalLiabilities`, `TotalAssets`, `MerkleRoot` (a fa gyökere, 32 bájt), `CommitmentCv` (az eszköz-elkötelezettség `C_A`), `ZkProof` (a teljes zk-STARK bizonyíték), `TreeDepth`, `Status`. Itt van az `AuditStatus` enum is. |
| `CryptographicSecret.cs` | A titkos anyag külön táblában: a Data Protectionnel titkosított vakító faktor (`BlindingFactorR`), 1:1 kapcsolatban az audittal. Azért külön, hogy a publikus státusz-végpont ne adhassa ki véletlenül. |
| `VerificationQuery.cs` | Naplózza, hogy ki mikor kérdezett rá egy azonosítóra (`ClientVerification` oldal). |
| `AuditProgressReport.cs` | A pipeline haladásjelentése a UI felé (`StepIndex`, `Message`, `TotalSteps = 4`). Itt van az `AuditProofResult` rekord is (tranzakció hash + Merkle-gyökér). |
| `EvmProofResponse.cs` | A Rust szerver válasza: `proof_elements[]` (a bizonyíték SHA-256 digestje a szerződésnek), `public_inputs[]` (C_A, gyökér, nonce), `proof` (a teljes bizonyíték), `depth`, `verified`. |

### 2.4 `web/` gyökér és konfiguráció

| Fájl | Felelősség |
|---|---|
| `Program.cs` | Belépési pont. DI-regisztráció (DbContextFactory, `IAuditProverService`, DataProtection, `ZkProofClient`, `HardhatProofSubmitterService`), induláskor `Database.Migrate()`, Blazor beállítás. A `UseMigrationsEndPoint` csak fejlesztői módban aktív. Serilog naplózás (lásd lent). Két minimal API végpont: `POST /api/audit/generate` (létrehoz egy `Prepared` auditot, háttérben elindítja a szolgáltatást, `202 Accepted`) és `GET /api/audit/status/{id}` (folyamatban: csak a státusz; `VerifiedOnChain`: a nyilvános bizonyíték: gyökér, eszköz-elkötelezettség, mélység, zk-STARK bizonyíték hexadecimálisan, **az eszközök és a kötelezettségek nélkül**). Rate limiting: IP-nként 5 `generate` / perc, 60 `status` / perc (`429`). A `status` végpont legalább 400 ms-ot vár, hogy az időzítésből ne derüljön ki, létezik-e az azonosító (`Helpers/ResponseTiming.cs`). |
| `Services/ZkProofClient.cs` | A Rust prover HTTP kliense. Hibás válasz esetén a törzset és a státuszkódot csak a naplóba írja, a hívó általános `HttpRequestException`-t kap. |

| `Data/SolvencyProofContextFactory.cs` | Design-time factory az `dotnet ef` parancsokhoz, fix LocalDB connection stringgel. |
| `SolvencyContractAbi.json` | A `ProofOfSolvency` szerződés ABI-ja, a `HardhatProofSubmitterService` tölti be. |
| `appsettings.json`, `appsettings.Development.json` | Connection string és a `Hardhat` szekció (RPC URL, privát kulcs, szerződéscím). |
| `SolvencyProof.csproj`, `SolvencyProof.slnx`, `dotnet-tools.json` | Projekt-, solution- és eszközleírók (EF tool). |
| `Properties/launchSettings.json`, `serviceDependencies*.json` | Indítási profilok és szolgáltatásfüggőségek (Visual Studio). |
| `wwwroot/app.css`, `favicon.png` | Statikus erőforrások. |

### 2.5 `web/Migrations/` – adatbázis-séma története

| Fájl | Tartalom |
|---|---|
| `20261019122747_InitialCreate` | Kezdeti séma. |
| `20261022085746_SeedAccounts` | A demó számlák betöltése. |
| `20261022111205_AlignSolvencyProofModel` | A `CryptographicSecret` tábla átnevezése `CryptographicSecrets`-re (kulcsok, indexek, idegen kulcs igazítása a modellhez). |
| `20261030083456_AddVerificationQueries` | A `VerificationQueries` tábla létrehozása. |
| `20261030092337_ProtectBlindingFactor` | A `BlindingFactorR` oszlop típusának módosítása (titkosított tárolás). |
| `20261112134911_AddZkProofTreeDepth` | A `TreeDepth` oszlop a `SolvencyAudit` táblán (a bizonyítás fájának mélysége; a régi auditoknál 0). |
| `*.Designer.cs`, `SolvencyProofContextModelSnapshot.cs` | EF által generált metaadatok és modell-pillanatkép. Kézzel nem szerkesztendők. |

### 2.6 `web/Components/` – Blazor UI

| Fájl | Felelősség |
|---|---|
| `App.razor`, `Routes.razor`, `_Imports.razor` | Az alkalmazás gyökere: HTML váz, útvonalválasztó, közös `using`-ok. |
| `Layout/MainLayout.razor`, `NavMenu.razor`, `ReconnectModal.razor` (+ `.css`, `.js`) | Oldalkeret, oldalsó menü (Home, Bizonyítás generálása, Azonosító hitelesítése) és a Blazor-kapcsolat megszakadásakor megjelenő ablak. |
| `Pages/ProofGeneration.razor` | **A valódi audit indítója.** Létrehoz egy `Prepared` auditot, meghívja az `IAuditProverService`-t, a `Progress<T>` jelentései alapján lépésenként mutatja a haladást (spinner, pipa, hiba), a végén kiírja a Merkle-gyökeret és a tranzakció hash-t. |
| `Pages/Home.razor` | Dashboard: összes kötelezettség (az ügyfélegyenlegek összege), összes eszköz (az utolsó `VerifiedOnChain` auditból) és fizetőképességi ráta kártyákon, modális "terminál" az audit élő folyamatáról. A "Kriptográfiai Audit Indítása" gomb a valódi `IAuditProverService`-t hívja, hiba esetén újrapróbálkozást kínál. |
| `Pages/ClientVerification.razor` | Az ügyfél beírja az azonosítóját. A rendszer az ügyfél levelétől a gyökérig vezető útvonalból újraszámolja a gyökeret, összeveti a **blokkláncról** olvasott gyökérrel, és ellenőrzi az audit zk-STARK bizonyítékát. Kiírja a levél hash-t, a gyökereket és az útvonalat (testvér-hash és részösszeg lépésenként), és naplózza a lekérdezést. Az ellenőrzés legalább 400 ms-ot vár (találat és nincs találat esetén egyaránt), és globálisan 20 ellenőrzés / perc a limit (a Blazor-körben nincs IP a HTTP rate limiterhez). |
| `Pages/AccountPages/` (`Index`, `Create`, `Details`, `Edit`, `Delete`) | Scaffoldolt CRUD oldalak az `Account` táblához. |
| `Pages/SolvencyAuditPages/` (`Index`, `Create`, `Details`, `Edit`, `Delete`) | Ugyanez a `SolvencyAudit` táblához. |
| `Pages/Error.razor`, `NotFound.razor` | Hiba- és 404 oldal. |

---

## 3. `calc/` – Rust prover (Axum)

| Fájl | Felelősség |
|---|---|
| `src/main.rs` | Az Axum szerver (alapból `127.0.0.1:3000`, a `PROVER_ADDR` környezeti változóval felülírható). `POST /api/v1/generate-proof`: validál, a nyers levelekből újraszámolja a gyökeret és az eszköz-elkötelezettséget, **409-cel elutasít, ha eltérnek a hívó értékeitől**, bizonyít, a bizonyítást még a válasz előtt ellenőrzi. `POST /api/v1/verify-proof`: csak nyilvános adatból ellenőriz. Az EVM-nek a bizonyíték SHA-256 digestjét adja, a teljes bizonyítékot külön mezőben. |
| `src/hash.rs` | Poseidon2 (Goldilocks, 12 széles) és a Merkle-összegfa, levél-, csomópont- és commitment-bemenetek, kódolások. A `write_csharp_constants` teszt ebből generálja a C# konstansokat. |
| `src/air.rs` | **Az AIR.** Minden sor egy teljes Poseidon2 permutációt tartalmaz, a sor típusát periodikus fázisoszlopok adják (levél, csomópont, elköteleződés, kitöltés). Kikényszeríti: minden levél érvényes útvonala a nyilvános gyökérig, a részösszegek helyességét, a 40 bites egyenleg-tartományt, `C_A = Poseidon2(a, r)` és `a − T ∈ [0, 2^62)` fennállását. |
| `src/trace.rs` | A titkos tanúból felépíti az AIR trace-ét és a nyilvános bemeneteket. |
| `src/stark.rs` | A zero-knowledge konfiguráció (Goldilocks, Poseidon2 Merkle-elköteleződés, `HidingFriPcs`, OS-entrópiából induló maszkok), `prove_solvency` és `verify_solvency`, valamint a támadási tesztek (hamis nyilvános bemenet, hamisított tartomány, manipulált bizonyíték). |
| `Cargo.toml`, `Cargo.lock` | Függőségek és verziórögzítés (a Plonky3 pontos verzióra rögzítve, mert az API még mozog). |
| `.vscode/launch.json`, `settings.json`, `.gitignore`, `.DS_Store` | Szerkesztő- és repóbeállítások. A `.DS_Store` macOS-szemét. |

---

## 4. `SolvencyProofContract/` – Hardhat és Solidity

| Fájl | Felelősség |
|---|---|
| `contracts/SolidityContract.sol` | **A fő szerződés, a `ProofOfSolvency`.** A `submitAndVerifyProof(institutionId, proof, publicInputs)` meghívja a `starkVerifier.verifyProof`-ot. Siker esetén `SolvencyVerified` eseményt emittál (a Merkle-gyökérrel), különben `VerificationFailed` eseményt, majd revert. Itt van az `ISTARKVerifier` interfész is. |
| `contracts/MockStarkVerifier.sol` | Álverifikátor: minden nem üres bizonyítékot elfogad (`proof.length > 0` és `publicInputs.length ≥ 2`). A valódi STARK-verifikátor helyett áll. |
| `contracts/Counter.sol`, `contracts/Counter.t.sol`, `test/Counter.ts`, `ignition/modules/Counter.ts` | A Hardhat sablon példa-szerződése, tesztjei és deploy-modulja. A solvency-logikához nem kapcsolódnak. |
| `scripts/deploy.ts` | Telepíti a `MockStarkVerifier`-t, majd a `ProofOfSolvency`-t (a verifikátor címével) a helyi láncra. A címet a `deployments/localhost.json` fájlba írja (`Hardhat:ContractAddress` alakban), amelyet a `web` induláskor felülírási konfigurációként olvas. |
| `scripts/send-op-tx.ts` | Hardhat sablon: egy OP-chain tranzakció küldése példaként. |
| `hardhat.config.ts` | Fordító (Solidity 0.8.34) és hálózatok (`localhost:8545`, szimulált láncok, Sepolia). |
| `package.json`, `tsconfig.json`, `.gitignore` | Node/TypeScript projektbeállítás. |
| `README.md`, `AGENTS.md`, `CLAUDE.md`, `.agents/`, `.claude/` | Dokumentáció és AI-asszisztens útmutatók a Hardhat használatához. |
| `node.log` | Futási napló. |

---

## 5. Gyökérszintű fájlok

| Fájl | Megjegyzés |
|---|---|
| `web.log`, `rust.log` | Régi futási naplók, nem tartoznak a verziókezelésbe. |
| `web/logs/` | A Serilog naplófájljai (`solvencyproof-*.log`, `telemetry-*.log`), napi rotációval. Nem kerülnek a verziókezelésbe (`*.log`). |
| `SolvencyProof.AppHost/` | .NET Aspire AppHost: a prover (`cargo run --release`), a Hardhat node és a `web` projekt együttes indítása. |


---

## 6. Futtatás sorrendje

1. `npx hardhat node` a `SolvencyProofContract/` mappában (8545-ös port).
2. `npx hardhat run scripts/deploy.ts --network localhost`. A cím a `SolvencyProofContract/deployments/localhost.json` fájlba kerül, a `web` ezt olvassa indításkor (a `web/appsettings.json` `Hardhat:ContractAddress` csak tartalék).
3. A prover indítása a `calc/` mappában: `cargo run --release` (a `--release` fontos, a bizonyítás debug módban lassú), 3000-es port.
   Ha a rustup-proxy késik, a toolchain `cargo.exe`-jét közvetlenül is hívhatod.
4. A `web/` alkalmazás indítása (`dotnet run`); induláskor automatikusan lefutnak a migrációk.

Alternatívaként az Aspire AppHost egy paranccsal indítja a provert, a Hardhat node-ot, telepíti a szerződést (`hardhat-deploy`) és a `web` alkalmazást: `dotnet run --project SolvencyProof.AppHost`. A Hardhat node újraindításakor a szerződés elvész, ezért a telepítést újra le kell futtatni, majd a `web`-et újraindítani.



## 7. Ismert hiányosságok és megfigyelések

- **Az on-chain ellenőrzés még álverifikátor.** A `MockStarkVerifier` minden nem üres bizonyítékot elfogad. A Plonky3 bizonyíték (~490 KB) túl nagy a calldatának és az EVM-en közvetlenül nem ellenőrizhető. Jelenleg a szerződés a bizonyíték SHA-256 digestjét kapja, a valódi ellenőrzést a Rust `verify-proof` végpont végzi (a Rust a bizonyítás után, a `ClientVerification` oldal pedig a tárolt bizonyítékon). Valódi on-chain ellenőrzéshez a bizonyítást SNARK-ba kellene csomagolni (például Groth16, bn254 precompile-lal).
- **A fa mérete kiszivárog.** A bizonyítás mérete és a `TreeDepth` nyilvános, így a számlák számának nagyságrendje (2-hatvány felső korlát) látszik. A fának legalább 2^6 helye van.
- **Az ügyfél útvonalában részösszegek látszanak.** A csomópont hash-e az alatta lévő összeget is köti, ezért az ügyfél az útvonal testvér-csomópontjainál a részösszegeket, a legfelső lépésnél a teljes kötelezettséget is megismeri. A lánc és a nyilvános végpont nem ad ki összeget, de a hitelesítő ügyfél igen. Ha a teljes kötelezettséget az ügyfelek elől is titkosan kellene tartani, a részösszegeket elrejtő elkötelezettségekre és tartomány-bizonyításokra kellene cserélni.
- **Az eszközök forrása.** A pipeline az `audit.TotalAssets` értékét az ügyfélegyenlegek összegéből veszi, ezért a ráta egy audit után mindig 100%. Valódi tartalékhoz (például tartalékcímek feletti aláírás vagy külső attesztáció) kötött eszközforrás még nincs.
- **Összegtartomány.** Az egyenlegek centben, 0 és 2^40 (kb. 11 milliárd egység) között lehetnek, legfeljebb 2^20 számla fér egy fába. A határokat az AIR is kikényszeríti.
- **A régi auditok** (a zk-STARK bevezetése előttiek) `TreeDepth = 0` értékűek, a hitelesítő oldal ezeket külön jelzi, és új auditot kér.
- **Holt kód:** a `ProofResponse.cs`, `PedersenHelper.cs`, `calc/src/handlers.rs`, az üres `web/SolvencyProofContext.cs` és a `Pages/Component.razor` fájlok eltávolításra kerültek.
- **Belső hibák nem jutnak ki a felhasználóhoz.** A UI oldalak (`ProofGeneration`, `ClientVerification`, `Home`) általános üzenetet mutatnak, a részletes kivételt a `ILogger` a naplóba írja. A Rust prover is általános szöveget küld (500 és a verifikáció hibája esetén), a részletek a szerver `stderr`-jén vannak. A fejlesztői `UseMigrationsEndPoint` csak fejlesztési módban fut.
- **Naplózás (Serilog).** A konzol és a `web/logs/solvencyproof-*.log` (Information, 30 nap) mellett a `web/logs/telemetry-*.log` (Debug, 14 nap) részletes naplót ír, a tulajdonságokkal együtt. A telemetria-napló belső működési részleteket tartalmaz, ezért szerveroldalon kell tartani, és nem szabad oda egyenlegeket vagy titkokat írni.
- **Be van drótozva** a `Hardhat:PrivateKey` az `appsettings.json`-ban. Ez most a Hardhat nyilvános teszt-kulcsa, éles környezetben titokkezelőből kell olvasni.

