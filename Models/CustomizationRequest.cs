using System.Text.Json;

namespace Minis.Api.Models;

public record CustomizationRequest(string ShopId, string Key, JsonElement Value);