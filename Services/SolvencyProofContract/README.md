# Sample Hardhat 3 Project (`mocha` and `ethers`)

This project showcases a Hardhat 3 project using `mocha` for tests and the `ethers` library for Ethereum interactions.

To learn more about Hardhat 3, please visit the [Getting Started guide](https://hardhat.org/docs/getting-started#getting-started-with-hardhat-3). To share your feedback, join our [Hardhat 3](https://hardhat.org/hardhat3-telegram-group) Telegram group or [open an issue](https://github.com/NomicFoundation/hardhat/issues/new) in our GitHub issue tracker.

## Project Overview

This example project includes:

- A simple Hardhat configuration file.
- Foundry-compatible Solidity unit tests.
- TypeScript integration tests using `mocha` and ethers.js
- Examples demonstrating how to connect to different types of networks, including locally simulating OP mainnet.

## Usage

The Hardhat compiler profile supports Solidity sources targeting both `^0.8.19` and `^0.8.34`.

### Running Tests

To run all the tests in the project, execute the following command:

```shell
npx hardhat test
```

You can also selectively run the Solidity or `mocha` tests:

```shell
npx hardhat test solidity
npx hardhat test mocha
```

### Make a deployment to Sepolia

This project includes an example Ignition module to deploy the contract. You can deploy this module to a locally simulated chain or to Sepolia.

To run the deployment to a local chain:

```shell
npx hardhat ignition deploy ignition/modules/Counter.ts
```

To run the deployment to Sepolia, you need an account with funds to send the transaction. The provided Hardhat configuration includes a Configuration Variable called `SEPOLIA_PRIVATE_KEY`, which you can use to set the private key of the account you want to use.

You can set the `SEPOLIA_PRIVATE_KEY` variable using the `hardhat-keystore` plugin or by setting it as an environment variable.

To set the `SEPOLIA_PRIVATE_KEY` config variable using `hardhat-keystore`:

```shell
npx hardhat keystore set SEPOLIA_PRIVATE_KEY
```

After setting the variable, you can run the deployment with the Sepolia network:

```shell
npx hardhat ignition deploy --network sepolia ignition/modules/Counter.ts
```

### C# backend on a local Hardhat node

Start a local node, then deploy the proof contracts to it:

```shell
npx hardhat node
npx hardhat run scripts/deploy.ts --network localhost
```

Set the deployed `SolvencyProofContract` address as `Hardhat:ProofContractAddress` in the web application's configuration. Set `Hardhat__PrivateKey` in the environment to the private key of a funded local Hardhat account. The C# backend signs transactions locally, so the node logs `eth_sendRawTransaction` and the decoded `SolvencyProofContract#submitAndVerifyProof` call.

For local development, the private key can be stored with .NET User Secrets instead of being added to `appsettings.json`:

```shell
dotnet user-secrets set "Hardhat:PrivateKey" "<private-key-from-hardhat-node>" --project ../web/SolvencyProof.csproj
```

Replace `<private-key-from-hardhat-node>` with the actual 64-character hexadecimal private key printed for an account by `npx hardhat node`; do not include the angle brackets or leave the placeholder text in the setting.

Alternatively, set the `Hardhat__PrivateKey` environment variable before starting the web project. The `Hardhat:JsonRpcUrl` and `Hardhat:ProofContractAddress` values remain in `Services/web/appsettings.json`.

The `/api/audit/generate` request now waits for proof generation and transaction submission to finish before responding.
