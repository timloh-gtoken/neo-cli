using Microsoft.AspNetCore.Mvc;

namespace Neo.Notifications
{
    #region snippet_ControllerSignature
    [Route("v1/tokens")]
    public class TokenController : ControllerBase
    #endregion
    {

        #region snippet_GetTokens
        [HttpGet]
        [ProducesResponseType(typeof(NotificationResult), 200)]
        [ProducesResponseType(404)]
        public IActionResult GetTokens(NotificationQuery pageQuery)
        {
            NotificationResult result = NotificationDB.Instance.GetTokens(pageQuery);

            result.Paginate(pageQuery);

            return Ok(result);
        }
        #endregion

    }
}