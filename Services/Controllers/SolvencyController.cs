using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace SolvencyProof.Controllers; 

[ApiController]
[Route("api/[controller]")] 
public class SolvencyController : ControllerBase
{
    private readonly ProofOfSolvencyDbContext _context;

    // Az ASP.NET Core automatikusan beinjektálja ide a DbContext-et a Program.cs alapján
    public SolvencyController(ProofOfSolvencyDbContext context)
    {
        _context = context;
    }

    // A HTTP GET kérés kezelése
    [HttpGet]
    public async Task<IActionResult> GetSolvencyRecords()
    {
        // Lekérdezzük az összes rekordot az adatbázisból
        var records = await _context.SolvencyRecords.ToListAsync();

        // Visszaadjuk HTTP 200 (OK) státuszkóddal
        return Ok(records);
    }
}