import { network } from "hardhat";

async function main() {
  const connection = await network.connect("hardhatMainnet");

  // Itt egy fiktív hetelesítő címet lehet megadni, vagy egy másik szerződés címét
  const verifierAddress = "0x0000000000000000000000000000000000000000";

  const ProofOfSolvency = await connection.ethers.getContractFactory("ProofOfSolvency");
  const contract = await ProofOfSolvency.deploy(verifierAddress);

  await contract.waitForDeployment();
  console.log(`Okosszerződés címe (Contract Address): ${contract.target}`);
}

main().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});