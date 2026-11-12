# SolvencyProof – Technológiai döntések és alternatívák

Ez a dokumentum rétegenként bemutatja, hogy a projekt egyes részeire milyen alternatív megoldások léteznek, és hogy a jelenlegi változatnak mik az előnyei és hátrányai. A fájlonkénti leírás az [ARCHITECTURE.md](ARCHITECTURE.md)-ben található.

> **Megjegyzés a döntési indokokról.** A repóból csak az látszik, hogy *mi* lett választva, az nem, hogy *miért*. Az alábbi "Miért valószínűleg ez a változat?" szakaszok a kódból levont következtetések. A tényleges szempontokat (tanulási cél, ismert technológia, határidő, előírás) a 8. szakasz tartalmazza, azt a projekt szerzőjének kell kitöltenie.

## 1. UI és webes réteg

| Megoldás | Előny | Hátrány |
|---|---|---|
| **Blazor Server** (jelenlegi) | Egy nyelv (C#). A UI közvetlenül éri el a DbContextet és a szolgáltatásokat, nincs külön API-réteg. A `Progress<T>` és a `StateHasChanged` miatt a lépésenkénti haladásjelzés egyszerű. | Minden felhasználóhoz állandó SignalR kapcsolat kell, kevésbé skálázódik, a szerver nélkül nem fut. |
| Blazor WebAssembly | Kliensoldali futás, kisebb szerverterhelés. | Az adatbázist és a privát kulcsot használó logika nem kerülhet a böngészőbe, ezért API kell hozzá. |
| React / Vue / Angular + REST API | Különálló frontend, a UI-fejlesztés elválik a háttértől. | Két technológia, külön API-szerződés és külön telepítés. |
| MVC / Razor Pages | Egyszerű, szerveroldali renderelés. | A valós idejű haladásjelzéshez SignalR vagy lekérdezés (polling) kell. |

**Miért valószínűleg a Blazor Server?** A `ProofGeneration.razor` a `Progress<T>` jelentéseit közvetlenül a UI állapotába írja, és a lapok az `IDbContextFactory`-t injektálják. Ezt Blazor Serverrel lehet a legkevesebb kóddal megoldani.

## 2. Az audit vezénylése (`AuditProverService`)

| Megoldás | Előny | Hátrány |
|---|---|---|
| **A kérésen belüli futtatás** (`ProofGeneration.razor`) és a **`fire-and-forget` háttér-`Task`** (`POST /api/audit/generate`) (jelenlegi) | Egyszerű, nincs külön infrastruktúra. | A folyamat újraindításakor a futó audit elvész, és `Proving` állapotban ragadhat. Az API-s ágon nincs `CancellationToken`. |
| `BackgroundService` + `Channel<T>` sor | A munka a kéréstől független, vezérelhető a leállítása. | Több kód, a tartósságot ez sem oldja meg. |
| **Hangfire / Quartz** | Tartós feladatsor, újrapróbálás, felület a feladatokhoz. | Külön tár és függőség. |

## 3. Bizonyítás (`calc/`)

| Megoldás | Megjegyzés |
|---|---|
| **Külön Rust szerver (Axum)** (jelenlegi) | A zk-STARK eszközök (Winterfell, Plonky2/3, RISC Zero, Stone/Cairo) Rustban élnek. A külön folyamat elválasztja a nehéz számítást a webszervertől. Hátrány: hálózati hívás és két futtatókörnyezet. |
| Rust natív könyvtár (FFI/P/Invoke) a C#-ból | Nincs hálózati hívás, de bonyolultabb a build és a hibakezelés. |
| **Cairo / StarkNet** | Natív STARK-támogatás és verifikátor-szerződések, de más lánc és más nyelv. |
| **Circom + snarkjs (Groth16)** | Kiforrott eszközlánc és olcsó on-chain ellenőrzés, de zk-SNARK, trusted setup kell hozzá. |
| **RISC Zero / SP1 (zkVM)** | A bizonyított program sima Rustban írható, a bizonyítékot a zkVM generálja. |

> **Megvalósítva:** a `calc/` valódi zk-STARK-ot állít elő **Plonky3**-mal (saját AIR, FRI, `HidingFriPcs` a zero-knowledge tulajdonsághoz). A Winterfell-t elvetettük, mert nem ad zero-knowledge bizonyítást, a RISC Zero-t és az SP1-et pedig a natív Windows-támogatás hiánya miatt. A bizonyítás továbbra sem ellenőrizhető közvetlenül az EVM-en, lásd az `ARCHITECTURE.md` ismert hiányosságait.

**Miért külön Rust szerver?** A zk-STARK ökoszisztéma Rustban van, ezért a prover külön szolgáltatás, amit a C# HTTP-n hív (`ZkProofClient`). A tanú (egyenlegek, eszközök, vakító tényező) csak loopbacken utazik az intézmény saját gépén belül.

## 4. Elköteleződés és Merkle-fa

| Megoldás | Megjegyzés |
|---|---|
| Pedersen-elköteleződés secp256k1-en (korábbi, BouncyCastle) | Homomorf, de a secp256k1 pontszorzás a STARK testében (Goldilocks) nagyon drága lenne, ezért lecseréltük. A korábbi `PedersenHelper.cs` törölve lett. |
| **Poseidon2-alapú elköteleződés** `C_A = Poseidon2(a, r)` (jelenlegi) | Zk-barát, a körben olcsón ellenőrizhető. Elrejtő (256 bites véletlen `r`) és kötő (ütközésállóság). |
| Merkle-fa SHA-256-tal (korábbi) | Egyszerű, de zk-körökben drága, és nem köti az egyenlegek összegét. |
| **Poseidon2 Merkle sum tree** (jelenlegi) | A csomópont hash-e az alatta lévő összeget is köti, így a gyökér a teljes kötelezettséget is rögzíti. Az ügyfél a saját útvonalát ellenőrizheti. |

## 5. Blokklánc

| Megoldás | Megjegyzés |
|---|---|
| **Hardhat helyi lánc + Nethereum** (jelenlegi) | Gyors, ingyenes, determinisztikus fejlesztői környezet. A Nethereum a .NET-es standard EVM-kliens. |
| Foundry / Anvil | Gyorsabb, a tesztek Solidityben írhatók (a `Counter.t.sol` már erre utal). |
| Testnet (Sepolia) | Valós hálózat, de lassabb. A `hardhat.config.ts`-ben már be van állítva. |
| Más lánc (L2, StarkNet) | Olcsóbb tranzakciók, más eszköz. |

## 6. Adattárolás és titkok védelme

| Megoldás | Megjegyzés |
|---|---|
| **SQL Server LocalDB + EF Core** (jelenlegi) | Jól illeszkedik a .NET-hez, migrációkkal. |
| PostgreSQL / SQLite | Platformfüggetlen alternatíva. |
| **ASP.NET Data Protection** a vakító faktorhoz (jelenlegi) | Beépített, egyszerű. A kulcsok a gépen élnek. |
| Azure Key Vault / HSM | Éles környezetben erősebb, a kulcs nem a gépen van. |

## 7. Miért valószínűleg ez a változat?

A fentiekből levonható következtetések, **nem biztos tudás**:

- **Egy nyelv a fő alkalmazásban:** a web, az adatbázis, a kriptográfia és a blokklánc-kliens mind C#/.NET.
- **A Rust csak ott, ahol kell:** a zk-STARK eszközök Rustban vannak, ezért a prover külön szolgáltatás.
- **Helyi Hardhat lánc:** gyors visszacsatolás, nincs szükség valódi láncra és ETH-ra.
- **Demó jelleg:** az on-chain verifikátor még álverifikátor (`MockStarkVerifier`), és a sablon `Counter` fájlok arra utalnak, hogy a cél az architektúra és a teljes folyamat bemutatása.

## 8. A szerző saját indokai (kitöltendő)

<!-- Ide kerülnek a tényleges szempontok. Példák: -->

- **Miért ez a technológiai stack?** _…_
- **Milyen követelmény vagy előírás befolyásolta a választást?** _…_
- **Mi volt a tanulási cél?** _…_
- **Melyik alternatívát vetetted el, és miért?** _…_
- **Mit változtatnál éles használatra?** _…_
