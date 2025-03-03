using Shouldly;
using WolverineWebApi;

namespace Wolverine.Http.Tests;

public class get_json_body : IntegrationContext
{
    public get_json_body(AppFixture fixture) : base(fixture)
    {
    }

    #region sample_get_json_happy_path

    [Fact]
    public async Task get_json_happy_path()
    {
        // This test is using Alba to run an end to end HTTP request
        // and interrogate the results
        var response = await Scenario(x =>
        {
            x.Get.Json(new Question { One = 3, Two = 4 }).ToUrl("/question/from-body");
            x.WithRequestHeader("accept", "application/json");
        });

        var result = await response.ReadAsJsonAsync<ArithmeticResults>();

        result.Product.ShouldBe(12);
        result.Sum.ShouldBe(7);
    }

    #endregion

    [Fact]
    public async Task get_json_happy_path_with_star_slash_star()
    {
        // This test is using Alba to run an end to end HTTP request
        // and interrogate the results
        var response = await Scenario(x =>
        {
            x.Get.Json(new Question { One = 3, Two = 4 }).ToUrl("/question/from-body");
            x.WithRequestHeader("accept", "*/*");
        });

        var result = await response.ReadAsJsonAsync<ArithmeticResults>();

        result.Product.ShouldBe(12);
        result.Sum.ShouldBe(7);
    }

    [Fact]
    public async Task get_json_happy_path_with_no_accept()
    {
        // This test is using Alba to run an end to end HTTP request
        // and interrogate the results
        var response = await Scenario(x =>
        {
            x.Get.Json(new Question { One = 3, Two = 4 }).ToUrl("/question/from-body");
        });

        var result = await response.ReadAsJsonAsync<ArithmeticResults>();

        result.Product.ShouldBe(12);
        result.Sum.ShouldBe(7);
    }

    [Fact]
    public async Task get_json_happy_path_with_accepts_problem_details()
    {
        // This test is using Alba to run an end to end HTTP request
        // and interrogate the results
        var response = await Scenario(x =>
        {
            x.Get.Json(new Question { One = 3, Two = 4 }).ToUrl("/question/from-body");
            x.WithRequestHeader("accept", "application/problem+json");
        });

        var result = await response.ReadAsJsonAsync<ArithmeticResults>();

        result.Product.ShouldBe(12);
        result.Sum.ShouldBe(7);
    }

    [Fact]
    public async Task get_json_garbage_get_400()
    {
        var response = await Scenario(x =>
        {
            x.Get.Text("garbage").ToUrl("/question/from-body");
            x.WithRequestHeader("content-type", "application/json");
            x.StatusCodeShouldBe(400);
        });
    }

    [Fact]
    public async Task get_text_get_415()
    {
        var response = await Scenario(x =>
        {
            x.Get.Text("garbage").ToUrl("/question/from-body");
            x.WithRequestHeader("content-type", "text/plain");
            x.StatusCodeShouldBe(415);
        });
    }

    [Fact]
    public async Task get_json_but_accept_text_get_406()
    {
        var response = await Scenario(x =>
        {
            x.Get.Json(new Question { One = 3, Two = 4 }).ToUrl("/question/from-body");
            x.WithRequestHeader("accept", "text/plain");
            x.StatusCodeShouldBe(406);
        });
    }
}
