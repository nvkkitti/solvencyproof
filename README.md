# SolvencyProof – zk-STARK alapú fizetőképesség-igazolás

A SolvencyProof egy demonstrációs rendszer, amellyel egy intézmény (pl. kriptotőzsde) úgy bizonyíthatja a fizetőképességét (eszközök ≥ kötelezettségek), hogy közben sem az ügyfelek egyenlegeit, sem a saját eszközállományát nem hozza nyilvánosságra. A bizonyítás **zk-STARK**-kal (Plonky3), **Poseidon2 Merkle sum tree**-vel és Poseidon2-alapú elköteleződésekkel történik, az eredmény pedig egy helyi Ethereum-láncra (Hardhat) kerül.

## A rendszer részei

| Komponens | Technológia | Szerep |
|---|---|---|
| `Services/web/` | ASP.NET Core Blazor Server (.NET 10), EF Core, Nethereum | Webes felület, adatbázis, audit-vezénylés, blokklánc-kliens |
| `Services/calc/` | Rust (Axum, Plonky3) | zk-STARK prover, HTTP-szerver a 3000-es porton (csak loopback) |
| `Services/SolvencyProofContract/` | Hardhat, Solidity, TypeScript | Helyi Ethereum-lánc (8545-ös port) és a `ProofOfSolvency` okosszerződés |
| `Services/SolvencyProof.AppHost/` | .NET Aspire | A fenti három komponens együttes indítása egy paranccsal |
| `Services/docs/` | – | [ARCHITECTURE.md](Services/docs/ARCHITECTURE.md) (fájlonkénti leírás), [TECHNOLOGY_DECISIONS.md](Services/docs/TECHNOLOGY_DECISIONS.md) (döntések és alternatívák) |

## Előfeltételek

A futtatáshoz Windowson a következők szükségesek:

1. **.NET 10 SDK** – <https://dotnet.microsoft.com/download/dotnet/10.0>
2. **SQL Server LocalDB** – a Visual Studio részeként általában telepítve van. Ellenőrzés: `sqllocaldb info` (ha a parancs létezik, rendben van). Külön is telepíthető a SQL Server Express telepítőjéből.
3. **Rust toolchain** (stabil, 1.85 vagy újabb, mert a projekt a 2024-es editiont használja) – <https://rustup.rs>. Ellenőrzés: `cargo --version`
4. **Node.js 22 LTS vagy újabb** (npm-mel) – <https://nodejs.org>. Ellenőrzés: `node --version`

## Első beüzemelés

Minden parancsot a `Services/` mappából indítva:

```powershell
cd Services
```

**1. A Hardhat-függőségek telepítése:**

```powershell
cd SolvencyProofContract
npm install
cd ..
```

**2. A tranzakció-aláíró privát kulcs beállítása.** A webalkalmazás a `Hardhat:PrivateKey` konfigurációs értékkel írja alá a bizonyítékot beküldő tranzakciót. Helyi láncon a Hardhat **nyilvánosan ismert, 0-s teszt-fiókjának** kulcsát kell megadni (ez nem titok, minden Hardhat-telepítés ugyanezt használja):

```powershell
dotnet user-secrets set "Hardhat:PrivateKey" "0xac0974bec39a17e36ba4a6b4d238ff944bacb478cbed5efcae784d7bf4f2ff80" --project web
```

(Alternatíva: a `web/appsettings.json` `Hardhat:PrivateKey` mezőjébe is beírható ugyanez az érték.)

**3. (Opcionális) A Rust prover előfordítása.** Az első fordítás több percig tarthat; ha előre lefuttatod, az indítás gyorsabb lesz:

```powershell
cd calc
cargo build --release
cd ..
```

Az adatbázist **nem kell** kézzel létrehozni: az alkalmazás induláskor automatikusan lefuttatja az EF Core migrációkat a LocalDB-n (`ProofOfSolvencyDb`), és három demó ügyfélszámlát is beszúr.

## Futtatás (ajánlott mód: Aspire AppHost)

Egyetlen parancs a `Services/` mappából:

```powershell
dotnet run --project SolvencyProof.AppHost
```

Ez sorban elindítja:
- a Rust provert (`cargo run --release`, 3000-es port),
- a Hardhat node-ot (8545-ös port),
- telepíti az okosszerződést (a cím a `SolvencyProofContract/deployments/localhost.json` fájlba kerül, a web innen olvassa),
- végül a webalkalmazást.

A konzolban megjelenik az **Aspire dashboard** linkje (bejelentkezési tokennel) – itt mind a négy erőforrás állapota és naplója látható. A webalkalmazás címe: **<https://localhost:7013>** (vagy <http://localhost:5174>).

> Az első indítás lassú lehet (Rust-fordítás + npm). Várd meg, amíg az Aspire dashboardon a `web` erőforrás is „Running” állapotba kerül.

### Alternatíva: kézi indítás (4 terminál)

Ha az Aspire nem indulna, a komponensek külön-külön is futtathatók, ebben a sorrendben:

```powershell
# 1. terminál – Hardhat node
cd Services/SolvencyProofContract
npx hardhat node

# 2. terminál – szerződés telepítése (a node indulása után)
cd Services/SolvencyProofContract
npx hardhat run scripts/deploy.ts --network localhost

# 3. terminál – Rust prover (a --release fontos, debug módban a bizonyítás nagyon lassú)
cd Services/calc
cargo run --release

# 4. terminál – webalkalmazás
cd Services/web
dotnet run --launch-profile https
```

## A működés ellenőrzése (bírálói forgatókönyv)

1. **Főoldal** (`/`): a dashboard mutatja az összes kötelezettséget (a seedelt ügyfélegyenlegek összegét), az összes eszközt és a fizetőképességi rátát. Első indításkor még nincs lezárt audit, ezért az eszköz-érték üres.
2. **Számlák** (`/accounts`): a három előre seedelt demó számla látható; itt újak is felvehetők, a meglévők szerkeszthetők – az audit mindig az aktuális adatbázis-állapotból dolgozik.
3. **Audit indítása**: a főoldalon a **„Kriptográfiai Audit Indítása”** gombbal (vagy a `/proof-generation` oldalon). A folyamat lépésenként, élőben követhető: Merkle sum tree építése → elköteleződések → zk-STARK bizonyítás a Rust proverrel → tranzakció beküldése a Hardhat-láncra. Sikeres futás végén megjelenik a **Merkle-gyökér** és a **tranzakció hash**. A bizonyítás gépfüggően néhány másodperctől 1–2 percig tarthat.
4. **Ellenőrzés a láncon**: a Hardhat node terminálján (Aspire-ban a `hardhat-node` erőforrás naplójában) látszik a beérkezett `submitAndVerifyProof` tranzakció.
5. **Publikus API**: az audit azonosítójával (a `/solvencyaudits` listából kimásolható) lekérdezhető a nyilvános bizonyíték – csak publikus adatokat ad vissza, egyenlegeket soha:

   ```
   GET https://localhost:7013/api/audit/status/{audit-guid}
   ```

   Sikeres audit esetén a válasz tartalmazza a `MerkleRoot`, `AssetsCommitment` és `ZkProof` hex értékeket. Új audit API-ból is indítható: `POST /api/audit/generate`.
6. **Ügyféloldali ellenőrzés** (`/clientverification`): az ügyfél a saját azonosítójával ellenőrizheti, hogy az egyenlege benne van-e az auditált Merkle-fában (inclusion proof).
7. **Auditok listája** (`/solvencyaudits`): a korábbi auditok állapota (`Prepared` → `Proving` → `VerifiedOnChain`, hiba esetén `Failed`) követhető.

## Hibaelhárítás

| Tünet | Ok és megoldás |
|---|---|
| `Configuration 'Hardhat:PrivateKey' must be a 32-byte hexadecimal private key` | A privát kulcs nincs beállítva – lásd a beüzemelés 2. lépését. |
| Az audit a tranzakció-beküldésnél hibázik | A Hardhat node újraindult, a telepített szerződés elveszett. Futtasd újra a deploy scriptet (`npx hardhat run scripts/deploy.ts --network localhost`), majd indítsd újra a webalkalmazást. |
| A bizonyítás percekig nem halad | A prover debug módban fut – mindig `cargo run --release`-zel indítsd. Az Aspire ezt automatikusan így teszi. |
| `Connection string 'SolvencyProofContext' not found` vagy LocalDB-hiba | Ellenőrizd, hogy a SQL Server LocalDB telepítve van-e: `sqllocaldb info`, szükség esetén `sqllocaldb start mssqllocaldb`. |
| Egy audit `Proving` állapotban ragadt | A webalkalmazás a bizonyítás közben újraindult; az adott audit elveszett. Indíts új auditot (a beragadt rekord a `/solvencyaudits` oldalon törölhető). |
| Portütközés (3000, 8545, 5174, 7013) | Zárd be a portot foglaló folyamatot, vagy indítsd újra az AppHostot. |

## Ismert korlátok

Ez a rendszer demonstrációs célú. Az on-chain ellenőrző jelenleg egy álverifikátor (`MockStarkVerifier`), azaz a zk-STARK bizonyítást a lánc nem ellenőrzi kriptográfiailag – a valódi ellenőrzés a Rust proverben történik. A további ismert hiányosságokat az [ARCHITECTURE.md](Services/docs/ARCHITECTURE.md) sorolja fel.
