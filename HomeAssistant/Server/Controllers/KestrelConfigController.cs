using HomeAssistant.Core;
using HomeAssistant.Extensions;
using HomeAssistant.Server.Responses;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using static HomeAssistant.Core.Enums;

namespace HomeAssistant.Server.Controllers {

	[Route("api/config")]
	public class KestrelConfigController : Controller{

		[HttpGet("coreconfig")]
		[Produces("application/json")]
		public ActionResult<GenericResponse<string>> GetCoreConfig (int authCode) {
			if (authCode == 0) {
				return BadRequest(new GenericResponse<string>("Authentication code cannot be equal to 0, or empty.",
					HttpStatusCodes.BadRequest, DateTime.Now));
			}

			if (!Tess.CoreInitiationCompleted) {
				return BadRequest(new GenericResponse<string>(
					"TESS core initiation isn't completed yet, please be patient while it is completed. retry after 20 seconds.",
					HttpStatusCodes.BadRequest, DateTime.Now));
			}

			if (authCode != Constants.KestrelAuthCode) {
				return BadRequest(new GenericResponse<string>("Authentication code is incorrect, you are not allowed to execute this command.",
					HttpStatusCodes.BadRequest, DateTime.Now));
			}

			return Ok(new GenericResponse<CoreConfig>(Tess.Config, HttpStatusCodes.OK, DateTime.Now));
		}

		[HttpGet("gpioconfig")]
		[Produces("application/json")]
		public ActionResult<GenericResponse<string>> GetGpioConfig (int authCode) {
			if (authCode == 0) {
				return BadRequest(new GenericResponse<string>("Authentication code cannot be equal to 0, or empty.",
					HttpStatusCodes.BadRequest, DateTime.Now));
			}

			if (Tess.IsUnknownOs) {
				return BadRequest(new GenericResponse<string>("Failed to fetch gpio config, Tess running on unknown OS.", Enums.HttpStatusCodes.BadRequest,
					DateTime.Now));
			}

			if (!Tess.CoreInitiationCompleted) {
				return BadRequest(new GenericResponse<string>(
					"TESS core initiation isn't completed yet, please be patient while it is completed. retry after 20 seconds.",
					HttpStatusCodes.BadRequest, DateTime.Now));
			}

			if (authCode != Constants.KestrelAuthCode) {
				return BadRequest(new GenericResponse<string>("Authentication code is incorrect, you are not allowed to execute this command.",
					HttpStatusCodes.BadRequest, DateTime.Now));
			}

			return Ok(new GenericResponse<List<GPIOPinConfig>>(Tess.Controller.GPIOConfig, HttpStatusCodes.OK, DateTime.Now));
		}

		[HttpPost("coreconfig")]
		[Consumes("application/json")]
		public ActionResult<GenericResponse<string>> SetCoreConfig (int authCode, [FromBody]CoreConfig config) {
			if (authCode == 0) {
				return BadRequest(new GenericResponse<string>("Authentication code cannot be equal to 0, or empty.",
					HttpStatusCodes.BadRequest, DateTime.Now));
			}

			if (!Tess.CoreInitiationCompleted) {
				return BadRequest(new GenericResponse<string>(
					"TESS core initiation isn't completed yet, please be patient while it is completed. retry after 20 seconds.",
					HttpStatusCodes.BadRequest, DateTime.Now));
			}

			if (authCode != Constants.KestrelAuthCode) {
				return BadRequest(new GenericResponse<string>("Authentication code is incorrect, you are not allowed to execute this command.",
					HttpStatusCodes.BadRequest, DateTime.Now));
			}

			if (config == null) {
				return BadRequest(new GenericResponse<string>("Config cant be empty.", HttpStatusCodes.BadRequest,
					DateTime.Now));
			}

			if (config.Equals(Tess.Config)) {
				return BadRequest(new GenericResponse<string>(
					"The new config and the current config is already same. update is unnecessary",
					HttpStatusCodes.BadRequest, DateTime.Now));
			}

			return Ok(new GenericResponse<CoreConfig>(Tess.Config.SaveConfig(config),
				"Config updated, please wait a while for tess to update the core values.", HttpStatusCodes.OK,
				DateTime.Now));
		}

		[HttpPost("gpioconfig")]
		[Consumes("application/json")]
		public ActionResult<GenericResponse<string>> SetGpioConfig (int authCode, [FromBody] GPIOConfigRoot config) {
			if (authCode == 0) {
				return BadRequest(new GenericResponse<string>("Authentication code cannot be equal to 0, or empty.",
					HttpStatusCodes.BadRequest, DateTime.Now));
			}

			if (Tess.IsUnknownOs) {
				return BadRequest(new GenericResponse<string>("Failed to update gpio config, Tess running on unknown OS.", Enums.HttpStatusCodes.BadRequest,
					DateTime.Now));
			}

			if (!Tess.CoreInitiationCompleted) {
				return BadRequest(new GenericResponse<string>(
					"TESS core initiation isn't completed yet, please be patient while it is completed. retry after 20 seconds.",
					HttpStatusCodes.BadRequest, DateTime.Now));
			}

			if (authCode != Constants.KestrelAuthCode) {
				return BadRequest(new GenericResponse<string>("Authentication code is incorrect, you are not allowed to execute this command.",
					HttpStatusCodes.BadRequest, DateTime.Now));
			}

			if (config == null) {
				return BadRequest(new GenericResponse<string>("Config cant be empty.", HttpStatusCodes.BadRequest,
					DateTime.Now));
			}

			if (config.Equals(Tess.Controller.GPIOConfigRoot)) {
				return BadRequest(new GenericResponse<string>(
					"The new config and the current config is already same. update is unnecessary",
					HttpStatusCodes.BadRequest, DateTime.Now));
			}

			return Ok(new GenericResponse<GPIOConfigRoot>(Tess.GPIOConfigHandler.SaveGPIOConfig(config),
				"Config updated, please wait a while for tess to update the core values.", HttpStatusCodes.OK,
				DateTime.Now));
		}
	}
}