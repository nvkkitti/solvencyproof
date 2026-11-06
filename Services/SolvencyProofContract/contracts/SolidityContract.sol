// SPDX-License-Identifier: MIT
pragma solidity ^0.8.19;

/**
 * @title ISTARKVerifier
 * @dev A külső generált zk-STARK verifikátor szerződés interfésze
 */
interface ISTARKVerifier {
    // Visszatérési értéke true, ha a matematikai verifikáció sikeres
    function verifyProof(
        uint256[] calldata proof, 
        uint256[] calldata publicInputs
    ) external view returns (bool);
}

/**
 * @title ProofOfSolvency
 * @dev On-chain verifikátor a fizetőképesség igazolására (v >= T)
 */
contract SolvencyProofContract {
    ISTARKVerifier public starkVerifier;
    
    // Események a döntés kimenetének rögzítésére
    event SolvencyVerified(bytes32 indexed institutionId, uint256 timestamp, bytes32 liabilitiesMerkleRoot);
    event VerificationFailed(bytes32 indexed institutionId, uint256 timestamp, string reason);

    /**
     * @param _verifierAddress A generált STARK verifikátor okosszerződés címe
     */
    constructor(address _verifierAddress) {
        starkVerifier = ISTARKVerifier(_verifierAddress);
    }

    /**
     * @notice Beküldi és ellenőrzi a fizetőképességi bizonyítékot
     * @param institutionId Az auditált intézmény egyedi azonosítója
     * @param proof A zk-STARK bizonyíték adatfolyama
     * @param publicInputs Nyilvános bemenetek (pl. [0] = Eszközök commitmentje, [1] = Kötelezettségek Merkle-gyökere)
     */
    function submitAndVerifyProof(
        bytes32 institutionId,
        uint256[] calldata proof,
        uint256[] calldata publicInputs
    ) external {
        require(proof.length > 0, "A bizonyitek nem lehet ures");
        require(publicInputs.length >= 2, "Hianyzo nyilvanos bemenetek");

        // 1. Döntés: matematikai verifikáció futtatása a STARK verifikátoron
        bool isValid = starkVerifier.verifyProof(proof, publicInputs);

        if (isValid) {
            // 2. Elfogad: v >= T teljesül
            // A publicInputs[1] általában a kötelezettség fa gyökere (T)
            bytes32 liabilitiesMerkleRoot = bytes32(publicInputs[1]);
            
            emit SolvencyVerified(institutionId, block.timestamp, liabilitiesMerkleRoot);
            
            // Itt a v és T tényleges értéke nem szerepel a kódban,
            // az "Adatminimalizálás" kriptográfiailag teljesült.
            
        } else {
            // 3. Elutasít: Érvénytelen fedezet
            emit VerificationFailed(institutionId, block.timestamp, "Ervenytelen STARK bizonyitek");
            revert("Ervenytelen fedezet: A matematikai verifikacio elbukott");
        }
    }
}