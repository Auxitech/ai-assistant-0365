using MailRouter.Web.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MailRouter.Web.Pages.Messages;

public class DetailsModel(AppDbContext db) : PageModel
{
    public ProcessedMessage Item { get; private set; } = null!;
    public List<ProcessedMessage> Thread { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var item = await db.Messages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id);
        if (item == null) return NotFound();
        Item = item;
        if (item.ConversationId != null)
            Thread = await db.Messages.AsNoTracking()
                .Where(m => m.ConversationId == item.ConversationId && m.Id != id)
                .OrderByDescending(m => m.ProcessedAt).Take(20).ToListAsync();
        return Page();
    }
}
