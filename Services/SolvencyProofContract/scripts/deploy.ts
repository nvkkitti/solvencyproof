import { network } from "hardhat";

const { ethers } = await network.create();

async function main() {
  // 1. A szimulált Verifikátor telepítése
  const MockVerifier = await ethers.getContractFactory("MockStarkVerifier");
  const mockVerifier = await MockVerifier.deploy();
  await mockVerifier.waitForDeployment();
  
  const verifierAddress = mockVerifier.target;
  console.log(`Mock verifikátor címe: ${verifierAddress}`);

  // 2. A SolvencyProofContract telepítése a Mock címével
  const SolvencyProofContract = await ethers.getContractFactory("SolvencyProofContract");
  const contract = await SolvencyProofContract.deploy(verifierAddress);
  await contract.waitForDeployment();
  
  console.log(`Fő okosszerződés címe: ${contract.target}`);
}

main().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});